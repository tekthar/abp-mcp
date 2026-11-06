import type { ToolsConfig } from "./profiles.js";

/** What the filter needs to know about a tool. */
export interface ToolFacts {
  readonly name: string;
  /**
   * The tool's HTTP method from the host's `/mcp/_discover`. `undefined` when the host's `_discover` was
   * unavailable; `null` when the tool has none (an AbpMcp dynamic tool).
   */
  readonly httpMethod?: string | null;
}

export type FilterDecision = { readonly visible: true } | { readonly visible: false; readonly reason: string };

/**
 * Decides which upstream tools a profile shows. It can only hide tools, never add them, and it is applied
 * to `tools/list` and again to every `tools/call`.
 *
 * - `deny` globs hide matching tools, and win over everything else.
 * - `allow` globs, when present, hide every tool that matches none of them.
 * - The `read-only` preset hides every tool whose HTTP method is not GET, including tools with no HTTP
 *   method and every tool when `_discover` is unavailable (it fails closed).
 * - `include` globs show tools the preset would hide, such as an ABP method named `ListAsync`, which ABP
 *   maps to POST although it only reads.
 *
 * This is a guardrail for the agent, not a security boundary: the host's permissions decide what the user
 * can do, through the bridge or not.
 */
export class ToolFilter {
  private readonly allow: readonly RegExp[];
  private readonly deny: readonly { glob: string; pattern: RegExp }[];
  private readonly include: readonly RegExp[];

  constructor(private readonly config: ToolsConfig) {
    this.allow = config.allow.map(globToRegExp);
    this.deny = config.deny.map((glob) => ({ glob, pattern: globToRegExp(glob) }));
    this.include = config.include.map(globToRegExp);
  }

  /** True when decisions need each tool's HTTP method, which only the host's `_discover` provides. */
  get needsHttpMethods(): boolean {
    return this.config.preset === "read-only";
  }

  get preset(): ToolsConfig["preset"] {
    return this.config.preset;
  }

  decide(tool: ToolFacts): FilterDecision {
    const denied = this.deny.find((entry) => entry.pattern.test(tool.name));
    if (denied) {
      return { visible: false, reason: `it matches the tools.deny pattern '${denied.glob}'` };
    }

    if (this.allow.length > 0 && !this.allow.some((pattern) => pattern.test(tool.name))) {
      return { visible: false, reason: "it matches none of the tools.allow patterns" };
    }

    if (this.config.preset === "read-only" && !this.include.some((pattern) => pattern.test(tool.name))) {
      if (tool.httpMethod === undefined) {
        return { visible: false, reason: "the read-only preset needs the host's /mcp/_discover, which is unavailable" };
      }

      if (tool.httpMethod === null || tool.httpMethod.toUpperCase() !== "GET") {
        return { visible: false, reason: "the read-only preset shows only tools backed by GET endpoints" };
      }
    }

    return { visible: true };
  }
}

/** A case-insensitive, anchored pattern for a glob where `*` matches any run of characters and `?` one. */
export function globToRegExp(glob: string): RegExp {
  let source = "";
  for (const char of glob) {
    if (char === "*") {
      source += ".*";
    } else if (char === "?") {
      source += ".";
    } else {
      source += char.replace(/[.+^${}()|[\]\\]/g, "\\$&");
    }
  }

  return new RegExp(`^${source}$`, "i");
}
