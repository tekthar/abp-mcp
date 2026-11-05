import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import {
  CallToolResultSchema,
  ListToolsResultSchema,
  McpError,
  ToolListChangedNotificationSchema,
  type CallToolRequest,
  type CallToolResult,
  type Implementation,
  type Tool,
} from "@modelcontextprotocol/sdk/types.js";
import type { FetchLike } from "../auth/oauth.js";
import type { TokenManager } from "../auth/token-manager.js";
import type { Logger } from "../log.js";
import type { Profile } from "../profiles.js";
import { callScope, createAuthenticatedFetch, type CallScope } from "./auth-fetch.js";
import { classifyCallError, isSessionExpired, isSignInError, MayHaveRunError, NotSentError } from "./errors.js";

export interface UpstreamOptions {
  readonly profile: Profile;
  readonly tokens: TokenManager;
  readonly log: Logger;
  readonly version: string;
  /** Whether a request may open the browser to sign in. */
  readonly interactive: boolean;
  readonly onToolListChanged: () => void;
  readonly baseFetch?: FetchLike;
  readonly connectTimeoutMs?: number;
}

/** An HTTP method per tool name, from the host's `/mcp/_discover`; `null` for a tool without one. */
export type HttpMethods = ReadonlyMap<string, string | null>;

/**
 * The MCP client side of the bridge: one Streamable HTTP session with the AbpMcp host, signed in as the user.
 *
 * Retries follow one rule: a request is sent again only when the host refused it before running anything.
 * That covers a 401 (handled in the fetch) and a 404 for an expired session (reconnect, then retry). A
 * `tools/call` that failed any other way after it was sent is reported as possibly run, never re-sent.
 */
export class Upstream {
  private session?: { readonly client: Client; readonly transport: StreamableHTTPClientTransport };
  private connecting?: Promise<Client>;
  private readonly fetch: FetchLike;
  private readonly host: string;

  /** The host's `serverInfo` and `instructions` from the last `initialize`. */
  serverInfo?: Implementation;
  instructions?: string;

  constructor(private readonly options: UpstreamOptions) {
    this.fetch = createAuthenticatedFetch({
      tokens: options.tokens,
      interactive: options.interactive,
      ...(options.baseFetch ? { baseFetch: options.baseFetch } : {}),
    });
    this.host = new URL(options.profile.url).host;
  }

  /** The authenticated fetch, for the host's other endpoints. */
  get authenticatedFetch(): FetchLike {
    return this.fetch;
  }

  get isConnected(): boolean {
    return this.session !== undefined;
  }

  /** Open the session if it is not open. Concurrent callers share one attempt. */
  connect(): Promise<Client> {
    if (this.session) {
      return Promise.resolve(this.session.client);
    }

    this.connecting ??= this.open().finally(() => {
      this.connecting = undefined;
    });
    return this.connecting;
  }

  /** Every tool the host lists for this user, across pages. */
  async listTools(): Promise<Tool[]> {
    return this.withReconnect(async (client) => {
      const tools: Tool[] = [];
      let cursor: string | undefined;
      do {
        const page = await client.request({ method: "tools/list", params: cursor ? { cursor } : {} }, ListToolsResultSchema);
        tools.push(...page.tools);
        cursor = page.nextCursor;
      } while (cursor);

      return tools;
    });
  }

  /** Forward a `tools/call`. See the class remarks for when it is retried. */
  async callTool(params: CallToolRequest["params"], signal?: AbortSignal): Promise<CallToolResult> {
    const client = await this.beforeSending(this.connect());
    try {
      return await this.callOnce(client, params, signal);
    } catch (error) {
      if (!isSessionExpired(error)) {
        throw error;
      }

      this.options.log.info(`The host ended the MCP session; reconnecting before calling '${params.name}'.`);
      return await this.callOnce(await this.beforeSending(this.reconnect()), params, signal);
    }
  }

  /** Opening the session happens before the call is sent, so a failure there means the call did not run. */
  private async beforeSending(session: Promise<Client>): Promise<Client> {
    try {
      return await session;
    } catch (error) {
      throw isSignInError(error) || error instanceof McpError ? error : new NotSentError(this.host, error);
    }
  }

  /** Each tool's HTTP method from `{url}/_discover`, or `undefined` when the host does not serve it. */
  async discoverHttpMethods(): Promise<HttpMethods | undefined> {
    try {
      const response = await this.fetch(`${this.options.profile.url}/_discover`, { headers: { accept: "application/json" } });
      if (!response.ok) {
        await response.body?.cancel().catch(() => undefined);
        this.options.log.warn(`${this.options.profile.url}/_discover returned ${response.status}; the read-only preset will hide every tool.`);
        return undefined;
      }

      const body = await response.json() as { tools?: { name?: unknown; http_method?: unknown }[] };
      const methods = new Map<string, string | null>();
      for (const tool of body.tools ?? []) {
        if (typeof tool.name === "string") {
          methods.set(tool.name, typeof tool.http_method === "string" ? tool.http_method : null);
        }
      }

      return methods;
    } catch (error) {
      if (isSignInError(error)) {
        throw error;
      }

      this.options.log.warn(`Could not read ${this.options.profile.url}/_discover; the read-only preset will hide every tool`, error);
      return undefined;
    }
  }

  /** End the session on the host (DELETE) and close the client. */
  async close(): Promise<void> {
    const session = this.session;
    this.session = undefined;
    if (session) {
      await session.transport.terminateSession().catch(() => undefined);
      await session.client.close().catch(() => undefined);
    }
  }

  private async open(): Promise<Client> {
    const client = new Client({ name: "abp-mcp-bridge", version: this.options.version }, { capabilities: {} });
    client.setNotificationHandler(ToolListChangedNotificationSchema, () => {
      this.options.onToolListChanged();
    });
    client.onerror = (error) => this.options.log.debug("Upstream transport error", error);

    const transport = new StreamableHTTPClientTransport(new URL(this.options.profile.url), { fetch: this.fetch });
    await client.connect(transport, { timeout: this.options.connectTimeoutMs ?? 30_000 });

    this.session = { client, transport };
    this.serverInfo = client.getServerVersion();
    this.instructions = client.getInstructions();
    client.onclose = () => {
      if (this.session?.client === client) {
        this.session = undefined;
      }
    };

    this.options.log.debug(`Connected to ${this.options.profile.url} (session ${transport.sessionId ?? "none"}).`);
    return client;
  }

  private async reconnect(): Promise<Client> {
    const stale = this.session;
    this.session = undefined;
    await stale?.client.close().catch(() => undefined);
    return this.connect();
  }

  /** For reads only: retry once on a new session after an expired session or a transport failure. */
  private async withReconnect<T>(operation: (client: Client) => Promise<T>): Promise<T> {
    try {
      return await operation(await this.connect());
    } catch (error) {
      if (error instanceof McpError || isSignInError(error)) {
        throw error;
      }

      this.options.log.info("Reconnecting to the host after a failed request", error);
      return await operation(await this.beforeSending(this.reconnect()));
    }
  }

  private async callOnce(client: Client, params: CallToolRequest["params"], signal?: AbortSignal): Promise<CallToolResult> {
    const controller = new AbortController();
    let transportFailure: unknown;
    const scope: CallScope = {
      settled: false,
      onTransportFailure: (error) => {
        if (!scope.settled && transportFailure === undefined) {
          transportFailure = error ?? new Error("The response stream failed.");
          controller.abort(transportFailure);
        }
      },
    };

    const forwardAbort = (): void => controller.abort(signal?.reason);
    signal?.addEventListener("abort", forwardAbort, { once: true });
    try {
      return await callScope.run(scope, () => client.request(
        { method: "tools/call", params },
        CallToolResultSchema,
        { signal: controller.signal, timeout: this.options.profile.timeoutSeconds * 1000, resetTimeoutOnProgress: true },
      )) as CallToolResult;
    } catch (error) {
      if (signal?.aborted) {
        throw error;
      }

      if (transportFailure !== undefined) {
        throw new MayHaveRunError(params.name, this.host, transportFailure);
      }

      if (isSessionExpired(error)) {
        throw error;
      }

      throw classifyCallError(error, params.name, this.host);
    } finally {
      scope.settled = true;
      signal?.removeEventListener("abort", forwardAbort);
    }
  }
}
