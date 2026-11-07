import { McpError, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import * as releaseSync from "@jitl/quickjs-wasmfile-release-sync";
import { newQuickJSWASMModuleFromVariant, type QuickJSContext, type QuickJSDeferredPromise, type QuickJSHandle, type QuickJSRuntime, type QuickJSSyncVariant, type QuickJSWASMModule } from "quickjs-emscripten-core";
import { hostCallTargets } from "../filter.js";
import { NotSentError } from "../upstream/errors.js";
import { cutString, MAX_SHAPED_CHARS, TRUNCATED, truncateJson } from "./truncate.js";

export const RUN_CODE_TOOL = "run_code";

export interface CodeLimits {
  /** Wall-clock budget for the whole run, tool calls included. */
  readonly timeoutMs: number;
  /** Most tool calls (`tools.<name>()`, `tools.call`) one run may make. */
  readonly maxCalls: number;
  /** Most `tools.search` and `tools.describe` lookups one run may make. */
  readonly maxLookups: number;
  readonly maxCodeChars: number;
  readonly memoryBytes: number;
  /** The result's JSON is cut to this many characters (about 6,000 tokens by default); logs and the error get the same budget. */
  readonly maxResultChars: number;
}

export const DEFAULT_CODE_LIMITS: CodeLimits = {
  timeoutMs: 60_000,
  maxCalls: 50,
  maxLookups: 50,
  maxCodeChars: 20_000,
  memoryBytes: 64 * 1024 * 1024,
  maxResultChars: 24_000,
};

/**
 * QuickJS's own stack limit, measured on WebAssembly's shadow stack. The same frames also use Node's stack, at two to
 * twenty-five times the bytes, and at a 1 MB limit Node's stack ran out first. Deep recursion now ends in QuickJS's own
 * catchable "stack overflow"; a host stack overflow that still escapes (deeply nested source) is contained below.
 */
const VM_STACK_BYTES = 256 * 1024;

// The longest strings a script may hand the bridge. Each is checked inside the VM before anything is copied out.
const MAX_NAME_CHARS = 256;
const MAX_ARGS_CHARS = 1_000_000;
const MAX_QUERY_CHARS = 1_000;
const MAX_NAMES_CHARS = 10_000;
const MAX_LOG_LINE_CHARS = 2_000;
const MAX_LOG_LINES = 200;
const MAX_CALL_ERROR_CHARS = 1_000;


/** What the script's `tools` object reaches. */
export interface CodeHost {
  call(name: string, args: Record<string, unknown> | undefined, signal: AbortSignal): Promise<CallToolResult>;
  search(query: string, limit?: number): Promise<unknown>;
  describe(names: readonly string[]): Promise<unknown>;
}

/**
 * One tool call the script made. `unknown` means it may have run on the host (the connection broke after it was
 * sent, the run ended while it was in flight, or the host failed in a way that can follow a commit): the agent must
 * not repeat it blindly. `refused` means it was never sent.
 */
export interface JournalEntry {
  readonly tool: string;
  /** For a host tool that runs other tools by name (`mcp_call_tool`, `mcp_call_tools`): the tools it was asked to run. */
  readonly targets?: readonly string[];
  status: "ok" | "error" | "refused" | "unknown";
  error?: string;
}

export interface CodeRun {
  readonly ok: boolean;
  readonly result?: unknown;
  readonly error?: string;
  readonly logs: string[];
  readonly calls: JournalEntry[];
}

/**
 * Run `code` and answer as an MCP tool result: the value, the logs and the call journal, as compact JSON text. The
 * value, the logs and the error are each cut to the result budget; the journal is always included.
 */
export async function runCodeTool(code: string, host: CodeHost, limits: Partial<CodeLimits> = {}, signal?: AbortSignal): Promise<CallToolResult> {
  const effective = { ...DEFAULT_CODE_LIMITS, ...limits };
  const run = await runCode(code, host, effective, signal);
  const calls = run.calls.map((call) => (call.error === undefined ? call : { ...call, error: cutString(call.error, MAX_CALL_ERROR_CHARS) }));
  const logs = boundLogs(run.logs, effective.maxResultChars);
  let result: unknown = null;
  if (run.ok) {
    try {
      result = truncateJson(run.result ?? null, effective.maxResultChars);
    } catch {
      // Shaping must never cost the answer its call journal.
      result = `${TRUNCATED} The result could not be cut to size.`;
    }
  }

  const body = run.ok
    ? { result, ...(logs.length ? { logs } : {}), calls }
    : { error: cutString(run.error ?? "The run failed.", effective.maxResultChars), ...(logs.length ? { logs } : {}), calls };
  // Compact: the budget bounds the text Claude receives, and indentation would grow with the result's depth.
  return { ...(run.ok ? {} : { isError: true }), content: [{ type: "text", text: JSON.stringify(body) }] };
}

// The package's types describe its CommonJS build; Node's ESM import gets the variant as the default export.
const variant = ((releaseSync as { default?: unknown }).default ?? releaseSync) as QuickJSSyncVariant;

let quickjs: Promise<QuickJSWASMModule> | undefined;

/** Stop reusing `used` (if it is still the shared module): the next run starts on a fresh one, and its memory goes. */
function dropModule(used: Promise<QuickJSWASMModule>): void {
  if (quickjs === used) {
    quickjs = undefined;
  }
}

/**
 * Run model-written code in a fresh QuickJS (WebAssembly) runtime. The code has no network, file or Node access: its
 * only way out is `tools`, whose calls take the bridge's normal path. Every call is journaled. Once the run is
 * cancelled, past its deadline or over, nothing more leaves the bridge.
 */
export async function runCode(code: string, host: CodeHost, limits: CodeLimits = DEFAULT_CODE_LIMITS, signal?: AbortSignal): Promise<CodeRun> {
  const logs: string[] = [];
  const calls: JournalEntry[] = [];
  if (code.length > limits.maxCodeChars) {
    return { ok: false, error: `The code is longer than ${limits.maxCodeChars} characters.`, logs, calls };
  }

  const modulePromise = (quickjs ??= newQuickJSWASMModuleFromVariant(variant));
  let module: QuickJSWASMModule;
  try {
    module = await modulePromise;
  } catch (error) {
    // Do not cache a failed load: the next run tries again.
    dropModule(modulePromise);
    throw error;
  }

  const abort = new AbortController();
  const onOuterAbort = (): void => abort.abort();
  signal?.addEventListener("abort", onOuterAbort);
  if (signal?.aborted) {
    // Cancelled before the listener was added (while the module loaded): no abort event will come now.
    abort.abort();
  }

  const deadline = Date.now() + limits.timeoutMs;
  const timedOut = (): boolean => Date.now() > deadline;
  const ended = (): boolean => abort.signal.aborted || timedOut();
  const timeoutMessage = (): string => signal?.aborted ? "The run was cancelled." : `The code did not finish within ${limits.timeoutMs} ms.`;
  if (abort.signal.aborted) {
    signal?.removeEventListener("abort", onOuterAbort);
    return { ok: false, error: timeoutMessage(), logs, calls };
  }

  let runtime: QuickJSRuntime;
  let vm: QuickJSContext;
  try {
    runtime = module.newRuntime();
    runtime.setMemoryLimit(limits.memoryBytes);
    runtime.setMaxStackSize(VM_STACK_BYTES);
    runtime.setInterruptHandler(ended);
    vm = runtime.newContext();
  } catch (error) {
    signal?.removeEventListener("abort", onOuterAbort);
    dropModule(modulePromise);
    return { ok: false, error: `The code runtime could not start: ${messageOf(error)}`, logs, calls };
  }

  const pending = new Set<QuickJSDeferredPromise>();
  let alive = true;
  let lookups = 0;

  // A host exception thrown out of WebAssembly (Node's own stack overflow, an emscripten abort) unwinds QuickJS without
  // its cleanup: the run ends at once, and its module is never used again.
  let fault: unknown;
  let onFault: () => void = () => undefined;
  const faulted = new Promise<"fault">((resolve) => {
    onFault = () => resolve("fault");
  });
  const guard = <T>(fn: () => T): T => {
    try {
      return fn();
    } catch (error) {
      if (fault === undefined && isHostFault(error)) {
        fault = error;
        onFault();
      }

      throw error;
    }
  };

  /** Settle a promise the script is awaiting, then run its continuations. Never throws: the run's outcome reports failures. */
  const settle = (deferred: QuickJSDeferredPromise, outcome: { value: string } | { error: string }): void => {
    pending.delete(deferred);
    if (!alive || fault !== undefined) {
      return;
    }

    try {
      guard(() => {
        const handle = "value" in outcome ? vm.newString(outcome.value) : vm.newError(outcome.error);
        try {
          if ("value" in outcome) {
            deferred.resolve(handle);
          } else {
            deferred.reject(handle);
          }
        } finally {
          handle.dispose();
        }

        runtime.executePendingJobs().dispose();
      });
    } catch {
      // Interrupted (deadline, cancel), out of VM memory, or a host fault that guard recorded.
    }
  };

  const hostPromise = (work: () => Promise<string>): QuickJSHandle => {
    const deferred = vm.newPromise();
    pending.add(deferred);
    void work()
      .then((value) => settle(deferred, { value }), (error: unknown) => settle(deferred, { error: messageOf(error) }))
      // settle never throws; this keeps any surprise from becoming an unhandled rejection that ends the bridge.
      .catch(() => undefined);
    return deferred.handle;
  };

  /** A string the script passed, read only after checking its length inside the VM. */
  const readString = (handle: QuickJSHandle, what: string, max: number): string => {
    if (vm.typeof(handle) !== "string") {
      throw new TypeError(`${what} must be a string.`);
    }

    const length = vm.getProp(handle, "length");
    let size: number;
    try {
      size = vm.getNumber(length);
    } finally {
      length.dispose();
    }

    if (size > max) {
      throw new Error(`${what} is longer than ${max} characters.`);
    }

    return vm.getString(handle);
  };

  const countLookup = (): void => {
    if (ended()) {
      throw new Error("The run has ended.");
    }

    if (++lookups > limits.maxLookups) {
      throw new Error(`The lookup limit (${limits.maxLookups} searches and describes) was reached.`);
    }
  };

  define(vm, "__call", (nameHandle, argsHandle) => {
    const name = readString(nameHandle, "A tool name", MAX_NAME_CHARS);
    if (ended()) {
      // Cancelled, past the deadline or finished: nothing more leaves the bridge.
      calls.push({ tool: name, status: "refused", error: "The run had ended; the call was not sent." });
      return hostPromise(async () => {
        throw new Error(`The run has ended; '${name}' was not called.`);
      });
    }

    if (calls.length >= limits.maxCalls) {
      return hostPromise(async () => {
        throw new Error(`The tool call limit (${limits.maxCalls}) was reached; '${name}' was not called.`);
      });
    }

    const args = parseArgs(readString(argsHandle, "The arguments", MAX_ARGS_CHARS));
    const targets = hostCallTargets(name, args)?.filter((target): target is string => typeof target === "string");
    const entry: JournalEntry = { tool: name, ...(targets?.length ? { targets } : {}), status: "unknown" };
    calls.push(entry);
    return hostPromise(async () => {
      let result: CallToolResult;
      try {
        result = await host.call(name, args, abort.signal);
      } catch (error) {
        // Not sent: refused. A JSON-RPC error while the run is live is the host's answer. Anything else may have run:
        // the run's own cancel (which the SDK also reports as an McpError) or a lost connection.
        entry.status = error instanceof NotSentError ? "refused" : !abort.signal.aborted && error instanceof McpError ? "error" : "unknown";
        entry.error = messageOf(error);
        throw error;
      }

      const outcome = classify(result);
      entry.status = outcome.status;
      if (outcome.status !== "ok") {
        entry.error = outcome.message;
        throw new Error(outcome.message);
      }

      return JSON.stringify(unwrap(result) ?? null);
    });
  });
  define(vm, "__search", (queryHandle, limitHandle) => {
    countLookup();
    const query = readString(queryHandle, "The search query", MAX_QUERY_CHARS);
    const limit = vm.typeof(limitHandle) === "number" ? vm.getNumber(limitHandle) : undefined;
    const bounded = limit !== undefined && Number.isInteger(limit) && limit >= 1 ? limit : undefined;
    return hostPromise(async () => JSON.stringify(await host.search(query, bounded)));
  });
  define(vm, "__describe", (namesHandle) => {
    countLookup();
    const names = JSON.parse(readString(namesHandle, "The tool names", MAX_NAMES_CHARS)) as unknown;
    if (!Array.isArray(names) || !names.every((name): name is string => typeof name === "string")) {
      throw new TypeError("tools.describe takes a tool name or an array of tool names.");
    }

    return hostPromise(async () => JSON.stringify(await host.describe(names)));
  });
  define(vm, "__log", (lineHandle) => {
    if (logs.length >= MAX_LOG_LINES || vm.typeof(lineHandle) !== "string") {
      return;
    }

    const length = vm.getProp(lineHandle, "length");
    const size = vm.getNumber(length);
    length.dispose();
    logs.push(size > MAX_LOG_LINE_CHARS ? `${TRUNCATED} a log line of ${size} characters` : vm.getString(lineHandle));
  });

  /** The VM value of a failed evaluation, as plain data; never throws for a value it cannot show. */
  const dumpError = (handle: QuickJSHandle): unknown => {
    try {
      return guard(() => vm.dump(handle) as unknown);
    } catch (error) {
      if (fault !== undefined) {
        throw error;
      }

      return "The code failed with a value that cannot be shown.";
    } finally {
      if (handle.alive) {
        handle.dispose();
      }
    }
  };

  const faultRun = (): CodeRun => ({
    ok: false,
    error: fault instanceof RangeError
      ? "The code ran out of stack: reduce the recursion or nesting depth."
      : "The code runtime failed, so the run was stopped.",
    logs,
    calls,
  });

  try {
    const prelude = guard(() => vm.evalCode(PRELUDE, "prelude.js"));
    if (prelude.error) {
      const error = dumpError(prelude.error);
      return { ok: false, error: ended() ? timeoutMessage() : `The code runtime could not start: ${dumpMessage(error)}`, logs, calls };
    }

    prelude.value.dispose();

    const evaluated = guard(() => vm.evalCode(wrap(code), "code.js"));
    if (evaluated.error) {
      const error = dumpError(evaluated.error);
      return { ok: false, error: ended() ? timeoutMessage() : `The code does not compile: ${dumpMessage(error)}`, logs, calls };
    }

    const promise = evaluated.value;
    const settled = vm.resolvePromise(promise);
    promise.dispose();
    guard(() => runtime.executePendingJobs().dispose());

    let timer: NodeJS.Timeout | undefined;
    const timeout = new Promise<"timeout">((resolve) => {
      timer = setTimeout(() => resolve("timeout"), Math.max(0, deadline - Date.now()));
    });
    // A listener added after an abort never runs: resolve at once for a run already cancelled.
    const aborted = new Promise<"aborted">((resolve) => {
      if (abort.signal.aborted) {
        resolve("aborted");
      } else {
        abort.signal.addEventListener("abort", () => resolve("aborted"), { once: true });
      }
    });
    const outcome = await Promise.race([settled, timeout, aborted, faulted]);
    clearTimeout(timer);

    if (outcome === "fault" || fault !== undefined) {
      return faultRun();
    }

    if (outcome === "timeout" || outcome === "aborted" || abort.signal.aborted) {
      abort.abort();
      if (outcome !== "timeout" && outcome !== "aborted") {
        outcome.dispose();
      }

      return { ok: false, error: timeoutMessage(), logs, calls };
    }

    if (outcome.error) {
      const error = dumpError(outcome.error);
      // The interrupt handler stops a CPU loop with an "interrupted" error: report it as the deadline.
      return { ok: false, error: timedOut() ? timeoutMessage() : dumpMessage(error), logs, calls };
    }

    const text = guard(() => vm.getString(outcome.value));
    outcome.value.dispose();
    if (text.length > MAX_SHAPED_CHARS) {
      // Too large to parse and shape cheaply on the bridge's only thread: cut its text instead.
      return { ok: true, result: cutString(text, limits.maxResultChars), logs, calls };
    }

    try {
      return { ok: true, result: JSON.parse(text) as unknown, logs, calls };
    } catch {
      return { ok: false, error: "The code's result could not be read as JSON.", logs, calls };
    }
  } catch (error) {
    if (fault !== undefined || isHostFault(error)) {
      fault ??= error;
      return faultRun();
    }

    throw error;
  } finally {
    signal?.removeEventListener("abort", onOuterAbort);
    abort.abort();
    // Late host answers are dropped from here on.
    alive = false;
    if (fault !== undefined) {
      // QuickJS was unwound mid-call: its state, and the module's stack pointer, cannot be trusted.
      dropModule(modulePromise);
    } else {
      release(runtime, vm, pending, modulePromise);
    }
  }
}

/**
 * Release a run: reject what the script still awaits without running any of its code (no interrupt can fire, no
 * continuation can loop), then free the context and runtime. A runtime QuickJS cannot free keeps its heap in the
 * module's memory, so the module is dropped with it.
 */
function release(
  runtime: QuickJSRuntime,
  vm: QuickJSContext,
  pending: Set<QuickJSDeferredPromise>,
  modulePromise: Promise<QuickJSWASMModule>,
): void {
  let released = true;
  try {
    runtime.removeInterruptHandler();
    for (const deferred of pending) {
      const handle = vm.newError("The run ended.");
      try {
        deferred.reject(handle);
      } finally {
        handle.dispose();
      }
    }
  } catch {
    released = false;
  }

  pending.clear();
  try {
    vm.dispose();
    runtime.dispose();
  } catch {
    released = false;
  }

  if (!released) {
    dropModule(modulePromise);
  }
}

/** The answer's logs, up to `budget` characters in total; the lines past it are counted, not kept. */
function boundLogs(lines: readonly string[], budget: number): string[] {
  const kept: string[] = [];
  let used = 0;
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index]!;
    if (used + line.length > budget) {
      kept.push(`${TRUNCATED} ${lines.length - index} more log line(s)`);
      break;
    }

    kept.push(line);
    used += line.length;
  }

  return kept;
}

// WebAssembly is a Node global the project's type libraries do not declare.
const WasmRuntimeError = (globalThis as unknown as { WebAssembly?: { RuntimeError?: abstract new () => Error } }).WebAssembly?.RuntimeError;

/** True for an exception thrown out of WebAssembly by the host: a stack overflow or an emscripten abort. */
function isHostFault(error: unknown): boolean {
  return error instanceof RangeError || (WasmRuntimeError !== undefined && error instanceof WasmRuntimeError);
}

/** The script's view: `tools` (calls, search, describe) and `console`. */
const PRELUDE = `
const __stringify = JSON.stringify;
const __host = {
  call: (name, args) => __call(String(name), __stringify(args === undefined ? {} : args)).then(JSON.parse),
  search: (query, limit) => __search(String(query), limit).then(JSON.parse),
  describe: (names) => __describe(__stringify(Array.isArray(names) ? names : [names])).then(JSON.parse),
};
globalThis.tools = new Proxy(__host, {
  get(target, key) {
    if (typeof key !== "string") return undefined;
    if (Object.prototype.hasOwnProperty.call(target, key)) return target[key];
    return (args) => target.call(key, args);
  },
});
const __format = (values) => values.map((v) => typeof v === "string" ? v : __stringify(v)).join(" ");
const __line = (values) => {
  const line = __format(values);
  return line.length > ${MAX_LOG_LINE_CHARS - 40} ? line.slice(0, ${MAX_LOG_LINE_CHARS - 40}) + " ${TRUNCATED}" : line;
};
globalThis.console = { log: (...v) => __log(__line(v)), info: (...v) => __log(__line(v)), warn: (...v) => __log(__line(v)), error: (...v) => __log(__line(v)) };
`;

/** The code as an async function (or a function body), run, its value serialized inside the sandbox as JSON text. */
function wrap(code: string): string {
  const trimmed = code.trim().replace(/;$/, "");
  const isFunction = /^(async\s*)?(\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>/.test(trimmed) || /^(async\s+)?function\b/.test(trimmed);
  const body = isFunction ? `(${trimmed})()` : `(async () => {\n${trimmed}\n})()`;
  // A value JSON cannot represent (a function, a symbol) becomes null rather than failing the answer.
  return `(async () => { const __value = await ${body}; const __text = __stringify(__value === undefined ? null : __value); return __text === undefined ? "null" : __text; })()`;
}

function define(vm: QuickJSContext, name: string, fn: (...args: QuickJSHandle[]) => QuickJSHandle | void): void {
  const handle = vm.newFunction(name, fn);
  vm.setProp(vm.global, name, handle);
  handle.dispose();
}

function parseArgs(text: string): Record<string, unknown> | undefined {
  const value = JSON.parse(text) as unknown;
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

/**
 * A tool result as a journal status. The bridge's own refusals carry a `[BRIDGE_...]` code. A host error that can follow
 * committed work, with no code (the SDK's text for an exception) or `[INTERNAL]`, is `unknown`: it may have run.
 */
function classify(result: CallToolResult): { status: JournalEntry["status"]; message: string } {
  if (!result.isError) {
    return { status: "ok", message: "" };
  }

  const message = textOf(result) || "The tool reported an error.";
  const code = message.match(/^\[([A-Za-z0-9_:.]+)\]/)?.[1];
  if (code === undefined || code === "INTERNAL" || code === "BRIDGE_CALL_OUTCOME_UNKNOWN") {
    return { status: "unknown", message };
  }

  if (code === "BRIDGE_TOOL_HIDDEN" || code === "BRIDGE_SIGN_IN_REQUIRED" || code === "BRIDGE_UNREACHABLE") {
    return { status: "refused", message };
  }

  return { status: "error", message };
}

/** What the script receives: the structured result, else the text parsed as JSON when it is JSON, else the text. */
function unwrap(result: CallToolResult): unknown {
  if (result.structuredContent !== undefined) {
    return result.structuredContent;
  }

  const content = result.content ?? [];
  if (content.length > 0 && content.every((block) => block.type === "text")) {
    const text = textOf(result);
    try {
      return JSON.parse(text) as unknown;
    } catch {
      return text;
    }
  }

  return content;
}

function textOf(result: CallToolResult): string {
  return (result.content ?? []).filter((block) => block.type === "text").map((block) => (block as { text: string }).text).join("\n");
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function dumpMessage(error: unknown): string {
  if (error && typeof error === "object") {
    const { name, message } = error as { name?: unknown; message?: unknown };
    if (typeof message === "string") {
      return typeof name === "string" && name !== "Error" ? `${name}: ${message}` : message;
    }
  }

  return String(error);
}
