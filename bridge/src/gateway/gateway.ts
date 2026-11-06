import type { CallToolResult, Tool } from "@modelcontextprotocol/sdk/types.js";
import { globToRegExp } from "../filter.js";
import type { Logger } from "../log.js";
import type { GatewayConfig } from "../profiles.js";
import { RUN_CODE_TOOL, runCodeTool, type CodeLimits } from "./code-mode.js";
import { DEFAULT_SEARCH_LIMIT, MAX_SEARCH_LIMIT, ToolSearchIndex } from "./search.js";

export const SEARCH_TOOL = "search_tools";
export const DESCRIBE_TOOL = "describe_tools";
export const CALL_TOOL = "call_tool";
const MAX_DESCRIBE = 20;

/** What the gateway needs from the bridge. */
export interface GatewayHost {
  /** The upstream tools the profile's filter allows. Throws when the host cannot be reached or signed in to. */
  tools(): Promise<Tool[]>;
  /** Each tool's HTTP method from the host's `/mcp/_discover`, when the bridge has read it. */
  httpMethods(): ReadonlyMap<string, string | null> | undefined;
  /** Call an upstream tool on the bridge's normal path: the profile's filter, the audit log, no replay. */
  call(name: string, args: Record<string, unknown> | undefined, signal?: AbortSignal): Promise<CallToolResult>;
  /** The host's name, for descriptions. */
  readonly hostName: string;
}

/**
 * Gateway mode (#120): Claude sees the pinned tools plus a way to find, describe and call every other tool the
 * profile allows, instead of every schema up front. With `code`, `run_code` (#121) calls several tools from one
 * sandboxed script.
 */
export class Gateway {
  private readonly pinned: readonly RegExp[];

  constructor(
    private readonly config: GatewayConfig,
    private readonly host: GatewayHost,
    private readonly log: Logger,
    private readonly codeLimits?: Partial<CodeLimits>,
  ) {
    this.pinned = config.pinned.map(globToRegExp);
  }

  /** True for the gateway's own tools; everything else goes to the host. */
  handles(name: string): boolean {
    return name === SEARCH_TOOL || name === DESCRIBE_TOOL || name === CALL_TOOL || (this.config.code && name === RUN_CODE_TOOL);
  }

  /** What `tools/list` shows: the pinned tools, then the gateway's tools. */
  list(allowed: readonly Tool[]): Tool[] {
    const own = this.ownTools(allowed.filter((tool) => !this.isPinned(tool.name)).length);
    const shadowed = allowed.filter((tool) => this.handles(tool.name));
    if (shadowed.length > 0) {
      this.log.warn(`The host has tools named like the gateway's (${shadowed.map((tool) => tool.name).join(", ")}); call them through ${CALL_TOOL}.`);
    }

    return [...allowed.filter((tool) => this.isPinned(tool.name) && !this.handles(tool.name)), ...own];
  }

  async call(name: string, args: Record<string, unknown> | undefined, signal?: AbortSignal): Promise<CallToolResult> {
    try {
      switch (name) {
        case SEARCH_TOOL:
          return json(await this.search(stringArg(args, "query"), optionalInteger(args, "limit")));
        case DESCRIBE_TOOL:
          return json(await this.describe(stringArrayArg(args, "names")));
        case CALL_TOOL:
          return await this.host.call(stringArg(args, "name"), objectArg(args, "arguments"), signal);
        case RUN_CODE_TOOL:
          return await runCodeTool(stringArg(args, "code"), {
            call: (tool, toolArgs, toolSignal) => this.host.call(tool, toolArgs, toolSignal),
            search: (query, limit) => this.search(query, limit),
            describe: (names) => this.describe(names),
          }, this.codeLimits, signal);
        default:
          return failure("BRIDGE_ERROR", `Unknown gateway tool '${name}'.`);
      }
    } catch (error) {
      if (error instanceof ArgumentError) {
        return failure("BRIDGE_INVALID_ARGUMENTS", error.message);
      }

      if (name === SEARCH_TOOL || name === DESCRIBE_TOOL) {
        // The tool list could not be read (not signed in, host unreachable): say so as a tool result.
        return failure("BRIDGE_ERROR", error instanceof Error ? error.message : String(error));
      }

      throw error;
    }
  }

  async search(query: string, limit = DEFAULT_SEARCH_LIMIT): Promise<{ results: ReturnType<ToolSearchIndex["search"]>; total: number }> {
    const tools = await this.host.tools();
    const results = new ToolSearchIndex(tools, this.host.httpMethods()).search(query, limit);
    return { results, total: tools.length };
  }

  async describe(names: readonly string[]): Promise<{ tools: object[]; notFound?: string[] }> {
    if (names.length > MAX_DESCRIBE) {
      throw new ArgumentError(`describe_tools takes at most ${MAX_DESCRIBE} names at a time.`);
    }

    const byName = new Map((await this.host.tools()).map((tool) => [tool.name, tool]));
    const tools: object[] = [];
    const notFound: string[] = [];
    for (const name of names) {
      const tool = byName.get(name);
      if (!tool) {
        notFound.push(name);
        continue;
      }

      const method = this.host.httpMethods()?.get(name);
      tools.push({
        name: tool.name,
        description: tool.description ?? "",
        inputSchema: tool.inputSchema,
        ...(tool.outputSchema ? { outputSchema: tool.outputSchema } : {}),
        ...(method ? { httpMethod: method } : {}),
        ...(tool.annotations ? { annotations: tool.annotations } : {}),
      });
    }

    return { tools, ...(notFound.length > 0 ? { notFound } : {}) };
  }

  private isPinned(name: string): boolean {
    return this.pinned.some((pattern) => pattern.test(name));
  }

  private ownTools(unlisted: number): Tool[] {
    const host = this.host.hostName;
    const tools: Tool[] = [
      {
        name: SEARCH_TOOL,
        description:
          `Find tools of ${host} that are not listed here (${unlisted} more). Returns names and one-line descriptions, ` +
          `best match first. Then use ${DESCRIBE_TOOL} for a tool's arguments and ${CALL_TOOL} to call it.`,
        inputSchema: {
          type: "object",
          properties: {
            query: { type: "string", description: "Words describing the tool, e.g. \"create issue\" or \"list projects\"." },
            limit: { type: "integer", minimum: 1, maximum: MAX_SEARCH_LIMIT, description: `How many matches (default ${DEFAULT_SEARCH_LIMIT}).` },
          },
          required: ["query"],
        },
        annotations: { readOnlyHint: true },
      },
      {
        name: DESCRIBE_TOOL,
        description: `The full description and the input (and output) JSON schemas of tools found with ${SEARCH_TOOL}.`,
        inputSchema: {
          type: "object",
          properties: { names: { type: "array", items: { type: "string" }, minItems: 1, maxItems: MAX_DESCRIBE } },
          required: ["names"],
        },
        annotations: { readOnlyHint: true },
      },
      {
        name: CALL_TOOL,
        description:
          `Call a tool of ${host} by name, with the arguments its schema describes (see ${DESCRIBE_TOOL}). ` +
          "Returns the tool's own result. The same rules apply as to a listed tool.",
        inputSchema: {
          type: "object",
          properties: {
            name: { type: "string" },
            arguments: { type: "object", additionalProperties: true },
          },
          required: ["name"],
        },
      },
    ];

    return this.config.code ? [...tools, runCodeDefinition(host)] : tools;
  }
}

function runCodeDefinition(host: string): Tool {
  return {
    name: RUN_CODE_TOOL,
    description: [
      `Run a short JavaScript async function that calls tools of ${host}, and get back only what it returns. ` +
      "Use it to chain or repeat calls without passing every intermediate result through the conversation.",
      "Inside the code:",
      "  await tools.<name>(args)       call a tool; resolves to its structured result or parsed JSON text, throws on a tool error",
      "  await tools.call(name, args)   the same, for any name",
      "  await tools.search(query)      like search_tools",
      "  await tools.describe([names])  like describe_tools",
      "  console.log(...)               returned in \"logs\"",
      "Example: async () => { const found = await tools.search(\"list projects\"); return found.results.map((t) => t.name); }",
      "There is no network, file or Node access, only tools. The result lists every tool call the code made (\"calls\"), " +
      "each ok, error or unknown (it may have run): do not repeat a call marked ok or unknown.",
    ].join("\n"),
    inputSchema: {
      type: "object",
      properties: { code: { type: "string", description: "An async arrow function, e.g. async () => { ... }." } },
      required: ["code"],
    },
  };
}

class ArgumentError extends Error {}

function stringArg(args: Record<string, unknown> | undefined, key: string): string {
  const value = args?.[key];
  if (typeof value !== "string" || value.trim() === "") {
    throw new ArgumentError(`'${key}' must be a non-empty string.`);
  }

  return value;
}

function optionalInteger(args: Record<string, unknown> | undefined, key: string): number | undefined {
  const value = args?.[key];
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== "number" || !Number.isInteger(value) || value < 1) {
    throw new ArgumentError(`'${key}' must be a positive integer.`);
  }

  return value;
}

function stringArrayArg(args: Record<string, unknown> | undefined, key: string): string[] {
  const value = args?.[key];
  if (!Array.isArray(value) || value.length === 0 || value.some((item) => typeof item !== "string")) {
    throw new ArgumentError(`'${key}' must be a non-empty array of tool names.`);
  }

  return value as string[];
}

function objectArg(args: Record<string, unknown> | undefined, key: string): Record<string, unknown> | undefined {
  const value = args?.[key];
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new ArgumentError(`'${key}' must be an object.`);
  }

  return value as Record<string, unknown>;
}

function json(value: unknown): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify(value, null, 2) }] };
}

function failure(code: string, message: string): CallToolResult {
  return { isError: true, content: [{ type: "text", text: `[${code}] ${message}` }] };
}
