import { McpError, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import * as releaseSync from "@jitl/quickjs-wasmfile-release-sync";
import { newQuickJSWASMModuleFromVariant, type QuickJSContext, type QuickJSDeferredPromise, type QuickJSHandle, type QuickJSSyncVariant, type QuickJSWASMModule } from "quickjs-emscripten-core";
import { truncateJson } from "./truncate.js";

export const RUN_CODE_TOOL = "run_code";

export interface CodeLimits {
  /** Wall-clock budget for the whole run, tool calls included. */
  readonly timeoutMs: number;
  readonly maxCalls: number;
  readonly maxCodeChars: number;
  readonly memoryBytes: number;
  /** The result's JSON is cut to this many characters (about 6,000 tokens by default). */
  readonly maxResultChars: number;
}

export const DEFAULT_CODE_LIMITS: CodeLimits = {
  timeoutMs: 60_000,
  maxCalls: 50,
  maxCodeChars: 20_000,
  memoryBytes: 64 * 1024 * 1024,
  maxResultChars: 24_000,
};

/** What the script's `tools` object reaches. */
export interface CodeHost {
  call(name: string, args: Record<string, unknown> | undefined, signal: AbortSignal): Promise<CallToolResult>;
  search(query: string, limit?: number): Promise<unknown>;
  describe(names: readonly string[]): Promise<unknown>;
}

/**
 * One tool call the script made. `unknown` means it may have run on the host (the connection broke after it
 * was sent, or the run ended while it was in flight): the agent must not repeat it blindly.
 */
export interface JournalEntry {
  readonly tool: string;
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

/** Run `code` and answer as an MCP tool result: the value, the logs and the call journal, as JSON text. */
export async function runCodeTool(code: string, host: CodeHost, limits: Partial<CodeLimits> = {}, signal?: AbortSignal): Promise<CallToolResult> {
  const effective = { ...DEFAULT_CODE_LIMITS, ...limits };
  const run = await runCode(code, host, effective, signal);
  const body = run.ok
    ? { result: truncateJson(run.result ?? null, effective.maxResultChars), ...(run.logs.length ? { logs: run.logs } : {}), calls: run.calls }
    : { error: run.error, ...(run.logs.length ? { logs: run.logs } : {}), calls: run.calls };
  return { ...(run.ok ? {} : { isError: true }), content: [{ type: "text", text: JSON.stringify(body, null, 2) }] };
}

// The package's types describe its CommonJS build; Node's ESM import gets the variant as the default export.
const variant = ((releaseSync as { default?: unknown }).default ?? releaseSync) as QuickJSSyncVariant;

let quickjs: Promise<QuickJSWASMModule> | undefined;

/**
 * Run model-written code in a fresh QuickJS (WebAssembly) runtime. The code has no network, file or Node access:
 * its only way out is `tools`, whose calls take the bridge's normal path. Every call is journaled.
 */
export async function runCode(code: string, host: CodeHost, limits: CodeLimits = DEFAULT_CODE_LIMITS, signal?: AbortSignal): Promise<CodeRun> {
  const logs: string[] = [];
  const calls: JournalEntry[] = [];
  if (code.length > limits.maxCodeChars) {
    return { ok: false, error: `The code is longer than ${limits.maxCodeChars} characters.`, logs, calls };
  }

  const module = await (quickjs ??= newQuickJSWASMModuleFromVariant(variant));
  const runtime = module.newRuntime();
  runtime.setMemoryLimit(limits.memoryBytes);
  runtime.setMaxStackSize(1024 * 1024);
  const abort = new AbortController();
  const onOuterAbort = (): void => abort.abort();
  signal?.addEventListener("abort", onOuterAbort);
  const deadline = Date.now() + limits.timeoutMs;
  runtime.setInterruptHandler(() => Date.now() > deadline || abort.signal.aborted);

  const vm = runtime.newContext();
  const pending = new Set<QuickJSDeferredPromise>();
  let alive = true;

  /** Settle a promise the script is awaiting, then run the script's continuations. */
  const settle = (deferred: QuickJSDeferredPromise, outcome: { value: string } | { error: string }): void => {
    pending.delete(deferred);
    if (!alive) {
      return;
    }

    const handle = "value" in outcome ? vm.newString(outcome.value) : vm.newError(outcome.error);
    if ("value" in outcome) {
      deferred.resolve(handle);
    } else {
      deferred.reject(handle);
    }

    handle.dispose();
    pump();
  };

  /** Run the script's queued continuations; an error there surfaces through the awaited promise. */
  const pump = (): void => {
    runtime.executePendingJobs().dispose();
  };

  const timedOut = (): boolean => Date.now() > deadline;
  const timeoutMessage = (): string => signal?.aborted ? "The run was cancelled." : `The code did not finish within ${limits.timeoutMs} ms.`;

  const hostPromise = (work: () => Promise<string>): QuickJSHandle => {
    const deferred = vm.newPromise();
    pending.add(deferred);
    work().then((value) => settle(deferred, { value }), (error: unknown) => settle(deferred, { error: messageOf(error) }));
    return deferred.handle;
  };

  define(vm, "__call", (nameHandle, argsHandle) => {
    const name = vm.getString(nameHandle);
    const args = parseArgs(vm.getString(argsHandle));
    if (calls.length >= limits.maxCalls) {
      return hostPromise(async () => {
        throw new Error(`The tool call limit (${limits.maxCalls}) was reached; '${name}' was not called.`);
      });
    }

    const entry: JournalEntry = { tool: name, status: "unknown" };
    calls.push(entry);
    return hostPromise(async () => {
      let result: CallToolResult;
      try {
        result = await host.call(name, args, abort.signal);
      } catch (error) {
        // A JSON-RPC error is the host's answer; anything else (cancelled, connection lost) may have run.
        entry.status = error instanceof McpError ? "error" : "unknown";
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
    const query = vm.getString(queryHandle);
    const limit = vm.typeof(limitHandle) === "number" ? vm.getNumber(limitHandle) : undefined;
    return hostPromise(async () => JSON.stringify(await host.search(query, limit)));
  });
  define(vm, "__describe", (namesHandle) => {
    const names = JSON.parse(vm.getString(namesHandle)) as string[];
    return hostPromise(async () => JSON.stringify(await host.describe(names)));
  });
  define(vm, "__log", (lineHandle) => {
    if (logs.length < 200) {
      logs.push(vm.getString(lineHandle));
    }
  });

  try {
    const prelude = vm.evalCode(PRELUDE, "prelude.js");
    vm.unwrapResult(prelude).dispose();

    const evaluated = vm.evalCode(wrap(code), "code.js");
    if (evaluated.error) {
      const error = vm.dump(evaluated.error) as unknown;
      evaluated.error.dispose();
      return { ok: false, error: timedOut() || abort.signal.aborted ? timeoutMessage() : `The code does not compile: ${dumpMessage(error)}`, logs, calls };
    }

    const promise = evaluated.value;
    const settled = vm.resolvePromise(promise);
    promise.dispose();
    pump();

    let timer: NodeJS.Timeout | undefined;
    const timeout = new Promise<"timeout">((resolve) => {
      timer = setTimeout(() => resolve("timeout"), Math.max(0, deadline - Date.now()));
    });
    const outcome = await Promise.race([settled, timeout]);
    clearTimeout(timer);

    if (outcome === "timeout" || abort.signal.aborted) {
      abort.abort();
      if (outcome !== "timeout") {
        outcome.dispose();
      }

      return { ok: false, error: timeoutMessage(), logs, calls };
    }

    if (outcome.error) {
      const error = vm.dump(outcome.error) as unknown;
      outcome.error.dispose();
      // The interrupt handler stops a CPU loop with an "interrupted" error: report it as the deadline.
      return { ok: false, error: timedOut() ? timeoutMessage() : dumpMessage(error), logs, calls };
    }

    const text = vm.getString(outcome.value);
    outcome.value.dispose();
    return { ok: true, result: JSON.parse(text) as unknown, logs, calls };
  } finally {
    signal?.removeEventListener("abort", onOuterAbort);
    abort.abort();
    // Settle what the script still awaits, so every promise handle is released before the context goes.
    for (const deferred of [...pending]) {
      settle(deferred, { error: "The run ended." });
    }

    alive = false;
    try {
      vm.dispose();
      runtime.dispose();
    } catch {
      // A script that ended mid-flight can leave QuickJS objects behind; the runtime is discarded either way.
    }
  }
}

/** The script's view: `tools` (calls, search, describe) and `console`. */
const PRELUDE = `
const __host = {
  call: (name, args) => __call(String(name), JSON.stringify(args === undefined ? {} : args)).then(JSON.parse),
  search: (query, limit) => __search(String(query), limit).then(JSON.parse),
  describe: (names) => __describe(JSON.stringify(Array.isArray(names) ? names : [names])).then(JSON.parse),
};
globalThis.tools = new Proxy(__host, {
  get(target, key) {
    if (typeof key !== "string") return undefined;
    if (key in target) return target[key];
    return (args) => target.call(key, args);
  },
});
const __format = (values) => values.map((v) => typeof v === "string" ? v : JSON.stringify(v)).join(" ");
globalThis.console = { log: (...v) => __log(__format(v)), info: (...v) => __log(__format(v)), warn: (...v) => __log(__format(v)), error: (...v) => __log(__format(v)) };
`;

/** The code as an async function (or a function body), run, its value serialized inside the sandbox. */
function wrap(code: string): string {
  const trimmed = code.trim().replace(/;$/, "");
  const isFunction = /^(async\s*)?(\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>/.test(trimmed) || /^(async\s+)?function\b/.test(trimmed);
  const body = isFunction ? `(${trimmed})()` : `(async () => {\n${trimmed}\n})()`;
  return `(async () => { const __value = await ${body}; return JSON.stringify(__value === undefined ? null : __value); })()`;
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

/** A tool result as a journal status; the bridge's own refusals carry a `[BRIDGE_...]` code. */
function classify(result: CallToolResult): { status: JournalEntry["status"]; message: string } {
  if (!result.isError) {
    return { status: "ok", message: "" };
  }

  const message = textOf(result) || "The tool reported an error.";
  const code = message.match(/^\[([A-Z_]+)\]/)?.[1];
  if (code === "BRIDGE_CALL_OUTCOME_UNKNOWN") {
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
