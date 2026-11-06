import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import {
  CallToolRequestSchema,
  ErrorCode,
  ListToolsRequestSchema,
  McpError,
  type CallToolResult,
  type Tool,
} from "@modelcontextprotocol/sdk/types.js";
import { toolErrorCode, type AuditLog, type AuditOutcome } from "./audit.js";
import { BreakerOpenError, NotSignedInError, OAuthError } from "./auth/errors.js";
import type { TokenManager } from "./auth/token-manager.js";
import { ToolFilter, type FilterDecision } from "./filter.js";
import { describeError, type Logger } from "./log.js";
import type { Profile } from "./profiles.js";
import { MayHaveRunError, NotSentError } from "./upstream/errors.js";
import type { HttpMethods, Upstream } from "./upstream/upstream.js";

export interface BridgeOptions {
  readonly profile: Profile;
  readonly version: string;
  readonly tokens: TokenManager;
  readonly upstream: Upstream;
  readonly log: Logger;
  readonly audit?: AuditLog;
  /** How long `tools/list` waits for a browser sign-in before answering with no tools. */
  readonly signInWaitMs?: number;
}

const DEFAULT_SIGN_IN_WAIT_MS = 20_000;

/**
 * The MCP server Claude talks to over stdio. It forwards `tools/list` and `tools/call` to the host, hides
 * what the profile's tool filter hides (on list and on call), passes `tools/list_changed` through, and tells
 * Claude which environment it is connected to.
 */
export class Bridge {
  readonly server: Server;
  private readonly filter: ToolFilter;
  private httpMethods: HttpMethods | undefined;
  private signIn: Promise<void> | undefined;

  constructor(private readonly options: BridgeOptions) {
    this.filter = new ToolFilter(options.profile.tools);
    const host = options.upstream.serverInfo;
    this.server = new Server(
      { name: host?.name ?? "abp-mcp", version: host?.version ?? options.version },
      {
        capabilities: { tools: { listChanged: true } },
        instructions: buildInstructions(options.profile, options.upstream.instructions),
      },
    );

    this.server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: await this.listTools() }));
    this.server.setRequestHandler(CallToolRequestSchema, async (request, extra) =>
      this.callTool(request.params.name, request.params.arguments, extra.signal));
  }

  /** Serve on `transport` (stdio in production). Closing it ends the host session too. */
  async start(transport: Transport): Promise<void> {
    this.server.onclose = () => {
      void this.options.upstream.close();
    };
    await this.server.connect(transport);
  }

  /** The host changed its tools: drop what the bridge cached and tell Claude to list again. */
  onUpstreamToolsChanged(): void {
    this.httpMethods = undefined;
    this.notifyToolsChanged();
  }

  async listTools(): Promise<Tool[]> {
    if (!(await this.ensureSignedIn())) {
      return [];
    }

    let tools: Tool[];
    try {
      tools = await this.options.upstream.listTools();
      if (this.filter.needsHttpMethods) {
        this.httpMethods = await this.options.upstream.discoverHttpMethods();
      }
    } catch (error) {
      this.options.log.error("tools/list failed", error);
      throw new McpError(ErrorCode.InternalError, userMessage(error, this.options.profile));
    }

    return tools.filter((tool) => this.decide(tool.name).visible);
  }

  async callTool(name: string, args: Record<string, unknown> | undefined, signal?: AbortSignal): Promise<CallToolResult> {
    const started = Date.now();
    const record = (outcome: AuditOutcome, errorCode?: string): void => this.options.audit?.record({
      tool: name, outcome, durationMs: Date.now() - started, ...(errorCode ? { errorCode } : {}),
    });

    let decision: FilterDecision;
    try {
      decision = await this.decideForCall(name);
    } catch (error) {
      record("failed", "BRIDGE_SIGN_IN_REQUIRED");
      return bridgeError(errorCode(error), userMessage(error, this.options.profile));
    }

    if (!decision.visible) {
      record("refused", "BRIDGE_TOOL_HIDDEN");
      return bridgeError("BRIDGE_TOOL_HIDDEN",
        `Tool '${name}' is not available through profile '${this.options.profile.name}': ${decision.reason}. ` +
        "It was not sent to the host.");
    }

    try {
      const result = await this.options.upstream.callTool({ name, ...(args ? { arguments: args } : {}) }, signal);
      const code = result.isError ? toolErrorCode(result) : undefined;
      record(result.isError ? "tool_error" : "ok", code);
      return result;
    } catch (error) {
      if (signal?.aborted) {
        record("cancelled");
        throw error;
      }

      if (error instanceof McpError) {
        // The host's own JSON-RPC error: pass it through unchanged.
        record("failed", String(error.code));
        throw error;
      }

      this.options.log.warn(`tools/call '${name}' failed`, error);
      record("failed", errorCode(error));
      return bridgeError(errorCode(error), userMessage(error, this.options.profile));
    }
  }

  private decide(name: string): FilterDecision {
    if (!this.filter.needsHttpMethods) {
      return this.filter.decide({ name });
    }

    const methods = this.httpMethods;
    return this.filter.decide({ name, httpMethod: methods ? (methods.get(name) ?? null) : undefined });
  }

  private async decideForCall(name: string): Promise<FilterDecision> {
    if (this.filter.needsHttpMethods && !this.httpMethods?.has(name)) {
      // Called before any tools/list, or for a tool the cache does not know: ask the host again.
      this.httpMethods = await this.options.upstream.discoverHttpMethods();
    }

    return this.decide(name);
  }

  /**
   * True when requests can carry a token. A browser profile that cannot sign in silently (never signed in, or
   * its stored sign-in expired) starts the browser sign-in in the background and waits a little; if the user
   * takes longer, the list is empty for now and Claude is told to list again once the sign-in completes.
   */
  private async ensureSignedIn(): Promise<boolean> {
    const { tokens, profile } = this.options;
    if (profile.auth.type !== "browser" || tokens.hasFreshToken()) {
      return true;
    }

    // Renew silently first, unless a browser sign-in is already running: joining it would wait for the user.
    if (!this.signIn && await this.hasToken()) {
      return true;
    }

    this.signIn ??= tokens.getAccessToken({ interactive: true }).then(
      () => {
        this.options.log.info(`Signed in to profile '${profile.name}'.`);
        this.notifyToolsChanged();
      },
      (error: unknown) => this.options.log.error(`Sign-in for profile '${profile.name}' failed`, error),
    ).finally(() => {
      this.signIn = undefined;
    });

    await Promise.race([
      this.signIn,
      new Promise<void>((resolve) => setTimeout(resolve, this.options.signInWaitMs ?? DEFAULT_SIGN_IN_WAIT_MS).unref()),
    ]);

    return tokens.hasFreshToken();
  }

  /** A token without opening the browser: cached, or renewed from the stored sign-in. */
  private async hasToken(): Promise<boolean> {
    try {
      await this.options.tokens.getAccessToken({ interactive: false });
      return true;
    } catch (error) {
      // Not signed in is handled by the caller; any other problem surfaces on the request itself.
      return !(error instanceof NotSignedInError);
    }
  }

  private notifyToolsChanged(): void {
    this.server.sendToolListChanged().catch((error: unknown) => this.options.log.debug("Could not send tools/list_changed", error));
  }
}

/** What Claude is told about this connection, after the host's own instructions. */
export function buildInstructions(profile: Profile, hostInstructions?: string): string {
  const lines: string[] = [];
  if (hostInstructions?.trim()) {
    lines.push(hostInstructions.trim(), "");
  }

  lines.push(`These tools reach ${new URL(profile.url).host} through the abp-mcp bridge, profile '${profile.name}' (${profile.stage}), signed in as the user.`);
  if (profile.stage === "production") {
    lines.push("PRODUCTION: this is a live system. Before any tool call that changes data, describe the change and get the user's explicit confirmation.");
  } else if (profile.stage === "staging") {
    lines.push("STAGING: a shared environment. Confirm with the user before changing data.");
  }

  if (profile.tools.preset === "read-only") {
    lines.push("Only read-only tools are available through this connection.");
  }

  if (profile.auth.type === "browser") {
    lines.push("If no tools are listed, the user has to finish signing in in the browser window the bridge opened. The tool list updates by itself afterwards.");
  }

  if (profile.notes) {
    lines.push(profile.notes);
  }

  return lines.join("\n");
}

function bridgeError(code: string, message: string): CallToolResult {
  return { isError: true, content: [{ type: "text", text: `[${code}] ${message}` }] };
}

function errorCode(error: unknown): string {
  if (error instanceof MayHaveRunError) {
    return "BRIDGE_CALL_OUTCOME_UNKNOWN";
  }

  if (error instanceof NotSentError) {
    return "BRIDGE_UNREACHABLE";
  }

  if (error instanceof NotSignedInError || error instanceof BreakerOpenError || error instanceof OAuthError) {
    return "BRIDGE_SIGN_IN_REQUIRED";
  }

  if (error instanceof StreamableHTTPError && (error.code === 401 || error.code === 403)) {
    return "BRIDGE_UNAUTHORIZED";
  }

  return "BRIDGE_ERROR";
}

/** A message that tells the user what happened and what to do. */
export function userMessage(error: unknown, profile: Profile): string {
  if (error instanceof StreamableHTTPError && (error.code === 401 || error.code === 403)) {
    return `The host rejected the sign-in for profile '${profile.name}' (HTTP ${error.code}). Run \`abp-mcp login ${profile.name}\`.`;
  }

  if (error instanceof OAuthError) {
    return `Sign-in for profile '${profile.name}' failed (${error.message}). Run \`abp-mcp doctor ${profile.name}\` for details.`;
  }

  if (error instanceof NotSentError || error instanceof MayHaveRunError || error instanceof NotSignedInError || error instanceof BreakerOpenError) {
    return error.message;
  }

  return `${describeError(error)}. Run \`abp-mcp doctor ${profile.name}\` for details.`;
}
