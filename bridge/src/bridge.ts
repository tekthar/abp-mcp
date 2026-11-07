import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import {
  CallToolRequestSchema,
  ErrorCode,
  InitializeRequestSchema,
  ListToolsRequestSchema,
  McpError,
  type CallToolResult,
  type InitializeRequest,
  type InitializeResult,
  type Tool,
} from "@modelcontextprotocol/sdk/types.js";
import { toolErrorCode, type AuditLog, type AuditOutcome } from "./audit.js";
import { BreakerOpenError, NotSignedInError, OAuthError } from "./auth/errors.js";
import type { TokenManager } from "./auth/token-manager.js";
import { hostCallTargets, isExactToolName, ToolFilter, type FilterDecision } from "./filter.js";
import { describeError, type Logger } from "./log.js";
import type { Profile } from "./profiles.js";
import { MayHaveRunError, NotSentError, withListToolsHint } from "./upstream/errors.js";
import type { HttpMethods, Upstream } from "./upstream/upstream.js";
import { after, openHostSession } from "./startup.js";
import type { CodeLimits } from "./gateway/code-mode.js";
import { Gateway } from "./gateway/gateway.js";

export interface BridgeOptions {
  readonly profile: Profile;
  readonly version: string;
  readonly tokens: TokenManager;
  readonly upstream: Upstream;
  readonly log: Logger;
  readonly audit?: AuditLog;
  /** How long `tools/list` waits for a browser sign-in before answering with no tools. */
  readonly signInWaitMs?: number;
  /**
   * How long Claude's `initialize` waits for the host session (and, for a browser profile that cannot sign in
   * silently, the browser sign-in) so it can carry the host's `serverInfo` and `instructions`.
   */
  readonly initializeWaitMs?: number;
  /** Limits for `run_code` in gateway mode (tests shorten them). */
  readonly codeLimits?: Partial<CodeLimits>;
}

const DEFAULT_SIGN_IN_WAIT_MS = 20_000;
export const DEFAULT_INITIALIZE_WAIT_MS = 20_000;

type InitializeHandler = (request: InitializeRequest) => Promise<InitializeResult>;

/**
 * The MCP server Claude talks to over stdio. It forwards `tools/list` and `tools/call` to the host, hides
 * what the profile's tool filter hides (on list and on call), passes `tools/list_changed` through, and tells
 * Claude which environment it is connected to.
 */
export class Bridge {
  readonly server: Server;
  private readonly filter: ToolFilter;
  private httpMethods: HttpMethods | undefined;
  /** The upstream tools the profile allows, from the last `tools/list`; the gateway searches these. */
  private allowedTools: Tool[] | undefined;
  private readonly gateway: Gateway | undefined;
  private signIn: Promise<void> | undefined;
  private initialized = false;

  constructor(private readonly options: BridgeOptions) {
    this.filter = new ToolFilter(options.profile.tools);
    const gateway = options.profile.tools.gateway;
    this.gateway = gateway
      ? new Gateway(gateway, {
        tools: () => this.allowedUpstreamTools(),
        httpMethods: () => this.httpMethods,
        call: (name, args, signal) => this.callTool(name, args, signal),
        hostName: new URL(options.profile.url).host,
      }, options.log, options.codeLimits)
      : undefined;
    this.server = new Server(
      { name: "abp-mcp", version: options.version },
      { capabilities: { tools: { listChanged: true } }, instructions: buildInstructions(options.profile) },
    );

    // The SDK's own initialize (version negotiation, client capabilities), answered once the host is known.
    const sdkInitialize = (this.server as unknown as { _oninitialize?: InitializeHandler })._oninitialize?.bind(this.server);
    if (!sdkInitialize) {
      throw new Error("This version of @modelcontextprotocol/sdk has no Server._oninitialize; the bridge cannot defer initialize.");
    }

    this.server.setRequestHandler(InitializeRequestSchema, async (request) => {
      const result = await sdkInitialize(request);
      const answer = { ...result, ...(await this.describeHost()) };
      this.initialized = true;
      return answer;
    });
    this.server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: await this.listTools() }));
    this.server.setRequestHandler(CallToolRequestSchema, async (request, extra) => {
      const { name, arguments: args } = request.params;
      return this.gateway?.handles(name)
        ? this.gateway.call(name, args, extra.signal)
        : this.callTool(name, args, extra.signal);
    });
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
    this.allowedTools = undefined;
    this.notifyToolsChanged();
  }

  async listTools(): Promise<Tool[]> {
    if (!(await this.ensureSignedIn())) {
      return [];
    }

    const allowed = await this.fetchAllowedTools();
    return this.gateway ? this.gateway.list(allowed) : allowed;
  }

  /** The cached allowed tools, or a fresh list from the host. */
  private async allowedUpstreamTools(): Promise<Tool[]> {
    if (this.allowedTools) {
      return this.allowedTools;
    }

    if (!(await this.ensureSignedIn())) {
      throw new NotSignedInError(`Profile '${this.options.profile.name}' is not signed in yet; finish the sign-in in the browser.`);
    }

    return this.fetchAllowedTools();
  }

  /** The host's tools, less what the profile's filter hides. */
  private async fetchAllowedTools(): Promise<Tool[]> {
    let tools: Tool[];
    try {
      tools = await this.options.upstream.listTools();
      if (this.filter.needsHttpMethods) {
        this.httpMethods = await this.options.upstream.discoverHttpMethods();
      }
    } catch (error) {
      this.options.log.error("tools/list failed", error);
      throw new McpError(ErrorCode.InternalError, withListToolsHint(userMessage(error, this.options.profile), error));
    }

    this.allowedTools = tools.filter((tool) => this.decide(tool.name).visible);
    return this.allowedTools;
  }

  async callTool(name: string, args: Record<string, unknown> | undefined, signal?: AbortSignal): Promise<CallToolResult> {
    const started = Date.now();
    const targets = hostCallTargets(name, args);
    const record = (outcome: AuditOutcome, errorCode?: string): void => {
      try {
        this.options.audit?.record({
          tool: name,
          ...(targets ? { targets: targets.map((target) => (typeof target === "string" ? target : String(target))) } : {}),
          outcome,
          durationMs: Date.now() - started,
          ...(errorCode ? { errorCode } : {}),
        });
      } catch (error) {
        // AuditLog.record does not throw; this guards the call's outcome against any audit sink that does.
        this.options.log.debug("Could not record the tool call in the audit log", error);
      }
    };

    let decision: FilterDecision;
    let refused = `Tool '${name}'`;
    try {
      decision = await this.decideForCall(name);
      // A host tool that runs other tools by name (AbpMcp's mcp_call_tool(s)) is only as visible as every tool it
      // would run: otherwise a hidden tool is one wrapper away.
      for (const target of decision.visible ? targets ?? [] : []) {
        const targetDecision: FilterDecision = isExactToolName(target)
          ? await this.decideForCall(target)
          : { visible: false, reason: "a tool name it carries is missing, or contains spaces or control characters" };
        if (!targetDecision.visible) {
          decision = targetDecision;
          refused = `Tool ${JSON.stringify(target)}, called through '${name}',`;
          break;
        }
      }
    } catch (error) {
      record("failed", "BRIDGE_SIGN_IN_REQUIRED");
      return bridgeError(errorCode(error), userMessage(error, this.options.profile));
    }

    if (!decision.visible) {
      record("refused", "BRIDGE_TOOL_HIDDEN");
      return bridgeError("BRIDGE_TOOL_HIDDEN",
        `${refused} is not available through profile '${this.options.profile.name}': ${decision.reason}. ` +
        "It was not sent to the host.");
    }

    let result: CallToolResult;
    try {
      result = await this.options.upstream.callTool({ name, ...(args ? { arguments: args } : {}) }, signal);
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

    // Outside the try: the host ran the call and answered, so nothing after this point may turn it into an error.
    record(result.isError ? "tool_error" : "ok", result.isError ? toolErrorCode(result) : undefined);
    return result;
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

    await Promise.race([this.startSignIn(), after(this.options.signInWaitMs ?? DEFAULT_SIGN_IN_WAIT_MS, undefined)]);
    return tokens.hasFreshToken();
  }

  /** Start the browser sign-in, or join the one running. Never rejects. */
  private startSignIn(): Promise<void> {
    const { tokens, profile } = this.options;
    this.signIn ??= tokens.getAccessToken({ interactive: true }).then(
      () => {
        this.options.log.info(`Signed in to profile '${profile.name}'.`);
        // Before Claude's initialize is answered, Claude has not listed anything yet: nothing to announce.
        if (this.initialized) {
          this.notifyToolsChanged();
        }
      },
      (error: unknown) => this.options.log.error(`Sign-in for profile '${profile.name}' failed`, error),
    ).finally(() => {
      this.signIn = undefined;
    });

    return this.signIn;
  }

  /**
   * The `serverInfo` and `instructions` for Claude's `initialize`: the host's, once its session is open. A
   * browser profile that cannot sign in silently starts the browser sign-in here (Claude lists the tools right
   * after initialize, which would start it anyway). If the host is not reachable, or the user has not finished
   * signing in, within `initializeWaitMs`, the answer is the bridge's own, and the instructions say so.
   */
  private async describeHost(): Promise<Pick<InitializeResult, "serverInfo" | "instructions">> {
    const { upstream, profile, tokens, log, version } = this.options;
    const waitMs = this.options.initializeWaitMs ?? DEFAULT_INITIALIZE_WAIT_MS;
    const deadline = Date.now() + waitMs;
    const open = async (): Promise<boolean> => {
      if (upstream.isConnected) {
        return true;
      }

      if (profile.auth.type === "browser" && !tokens.hasFreshToken() && (this.signIn || !(await this.hasToken()))) {
        await this.startSignIn();
        if (!tokens.hasFreshToken()) {
          return false;
        }
      }

      return openHostSession(upstream, deadline, log);
    };

    const opened = await Promise.race([open(), after(waitMs, false)]);
    const host = opened ? upstream.serverInfo : undefined;
    if (!host) {
      log.info(`The session with ${new URL(profile.url).host} was not open within ${waitMs} ms; initialize carries the bridge's own information.`);
    }

    return {
      serverInfo: host ?? { name: "abp-mcp", version },
      instructions: buildInstructions(profile, host ? upstream.instructions : undefined, { hostMissing: !host }),
    };
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
export function buildInstructions(profile: Profile, hostInstructions?: string, options: { hostMissing?: boolean } = {}): string {
  const lines: string[] = [];
  if (hostInstructions?.trim()) {
    lines.push(hostInstructions.trim(), "");
  }

  const host = new URL(profile.url).host;
  lines.push(`These tools reach ${host} through the abp-mcp bridge, profile '${profile.name}' (${profile.stage}), signed in as the user.`);
  if (options.hostMissing) {
    lines.push(`The bridge could not reach ${host} (or sign-in was not finished) when this connection started, so the application's own instructions are missing from this session. Restarting this MCP server loads them once the host is reachable.`);
  }
  if (profile.stage === "production") {
    lines.push("PRODUCTION: this is a live system. Before any tool call that changes data, describe the change and get the user's explicit confirmation.");
  } else if (profile.stage === "staging") {
    lines.push("STAGING: a shared environment. Confirm with the user before changing data.");
  }

  if (profile.tools.preset === "read-only") {
    lines.push("Only read-only tools are available through this connection.");
  }

  if (profile.tools.gateway) {
    lines.push(
      "Only some of the host's tools are listed. Find the others with search_tools, read their arguments with describe_tools, " +
      "and call them with call_tool" + (profile.tools.gateway.code ? "; run_code calls several tools from one short script." : "."));
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
