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
  /** Requests in flight per client, so a replaced session is closed only once they have settled. */
  private readonly active = new Map<Client, number>();
  /** Replaced clients waiting for their requests to settle before they are closed. */
  private readonly retiring = new Set<Client>();
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
      return await this.callOnce(await this.beforeSending(this.reconnect(client)), params, signal);
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

    for (const client of this.retiring) {
      await client.close().catch(() => undefined);
    }

    this.retiring.clear();
  }

  private async open(): Promise<Client> {
    const client = new Client({ name: "abp-mcp", version: this.options.version }, { capabilities: {} });
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

  /**
   * Replace the session `failed` belongs to. When another request already replaced it (or it closed), the
   * current session is used as is: closing it would break the requests already running on it.
   */
  private async reconnect(failed: Client | undefined): Promise<Client> {
    const stale = this.session;
    if (failed && stale?.client === failed) {
      this.session = undefined;
      await this.retire(stale.client);
    }

    return this.connect();
  }

  /**
   * Close a replaced client, or, while other requests are still in flight on it, once they settle: closing it
   * now would abort them, and a `tools/call` aborted after it was sent is reported as possibly run.
   */
  private async retire(client: Client): Promise<void> {
    if (this.active.has(client)) {
      this.retiring.add(client);
      return;
    }

    await client.close().catch(() => undefined);
  }

  /** Run a request on `client`, counted as in flight on it. */
  private async track<T>(client: Client, request: () => Promise<T>): Promise<T> {
    this.active.set(client, (this.active.get(client) ?? 0) + 1);
    try {
      return await request();
    } finally {
      const left = (this.active.get(client) ?? 1) - 1;
      if (left > 0) {
        this.active.set(client, left);
      } else {
        this.active.delete(client);
        if (this.retiring.delete(client)) {
          void client.close().catch(() => undefined);
        }
      }
    }
  }

  /** For reads only: retry once on a new session after an expired session or a transport failure. */
  private async withReconnect<T>(operation: (client: Client) => Promise<T>): Promise<T> {
    let client: Client | undefined;
    try {
      const first = client = await this.connect();
      return await this.track(first, () => operation(first));
    } catch (error) {
      if (error instanceof McpError || isSignInError(error)) {
        throw error;
      }

      this.options.log.info("Reconnecting to the host after a failed request", error);
      const next = await this.beforeSending(this.reconnect(client));
      return await this.track(next, () => operation(next));
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
      return await this.track(client, () => callScope.run(scope, () => client.request(
        { method: "tools/call", params },
        CallToolResultSchema,
        { signal: controller.signal, timeout: this.options.profile.timeoutSeconds * 1000, resetTimeoutOnProgress: true },
      ) as Promise<CallToolResult>));
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
