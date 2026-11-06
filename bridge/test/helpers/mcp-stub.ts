import { AsyncLocalStorage } from "node:async_hooks";
import { randomUUID } from "node:crypto";
import http from "node:http";
import type { AddressInfo } from "node:net";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { json, readBody } from "./oauth-stub.js";

export interface McpStubOptions {
  /** Accept a bearer token. Omit for a host that allows anonymous access. */
  readonly isValidToken?: (token: string | undefined) => boolean;
  /** The authorization server named in the protected-resource metadata. */
  readonly issuer?: string;
  /** Serve `/mcp/_discover`. */
  readonly discover?: boolean;
  /**
   * Where the host advertises its RFC 9728 metadata: named in the 401 challenge (the default), only at the
   * well-known location, or nowhere.
   */
  readonly resourceMetadata?: "challenge" | "well-known" | "none";
  /** Serve `/mcp/_explain` reporting these tools with undefined permissions. */
  readonly undefinedPermissions?: readonly { tool: string; permission: string }[];
}

const HTTP_METHODS: Record<string, string | null> = {
  Book_Get: "GET",
  Book_List: "POST",
  Book_Delete: "DELETE",
  Book_Crash: "POST",
  Book_CrashMidStream: "POST",
  Book_Slow: "POST",
  Library_Overview: null,
};

/**
 * An AbpMcp-like host: stateful Streamable HTTP sessions behind bearer auth, `/mcp/_discover`, RFC 9728
 * metadata, and tools that count executions or misbehave on purpose.
 */
export class McpStub {
  private server?: http.Server;
  private readonly sessions = new Map<string, { transport: StreamableHTTPServerTransport; mcp: McpServer }>();
  private readonly current = new AsyncLocalStorage<http.ServerResponse>();

  /** How many times each tool body ran. */
  readonly executions = new Map<string, number>();
  /** Authorization header of every request, in order. */
  readonly authorizations: (string | undefined)[] = [];
  url = "";
  origin = "";
  /** Drop the connection on this many `initialize` requests before accepting one. */
  failInitializations = 0;
  /** Every `initialize` request that arrived, accepted or dropped. */
  initializeAttempts = 0;
  /** Answer `tools/list` with -32603, as AbpMcp 0.4.0 did when one tool had an undefined permission. */
  failToolsList = false;

  constructor(private readonly options: McpStubOptions = {}) {}

  async start(): Promise<this> {
    this.server = http.createServer((request, response) => void this.handle(request, response));
    await new Promise<void>((resolve) => this.server!.listen(0, "127.0.0.1", resolve));
    this.origin = `http://127.0.0.1:${(this.server.address() as AddressInfo).port}`;
    this.url = `${this.origin}/mcp`;
    return this;
  }

  async stop(): Promise<void> {
    for (const { transport } of this.sessions.values()) {
      await transport.close().catch(() => undefined);
    }

    this.server?.closeAllConnections();
    await new Promise<void>((resolve) => this.server?.close(() => resolve()) ?? resolve());
  }

  /** Forget every session, as an AbpMcp host does after its idle timeout. */
  async expireSessions(): Promise<void> {
    for (const { transport } of this.sessions.values()) {
      await transport.close().catch(() => undefined);
    }

    this.sessions.clear();
  }

  /** Tell every connected client that the tool list changed. */
  async notifyToolListChanged(): Promise<void> {
    for (const { mcp } of this.sessions.values()) {
      mcp.sendToolListChanged();
    }
  }

  ran(tool: string): number {
    return this.executions.get(tool) ?? 0;
  }

  private count(tool: string): void {
    this.executions.set(tool, this.ran(tool) + 1);
  }

  private async handle(request: http.IncomingMessage, response: http.ServerResponse): Promise<void> {
    const url = new URL(request.url ?? "/", this.origin);
    const advertise = this.options.resourceMetadata ?? "challenge";
    if (url.pathname === "/.well-known/oauth-protected-resource/mcp" && advertise !== "none") {
      return json(response, 200, { resource: this.url, authorization_servers: [this.options.issuer], scopes_supported: ["Library", "offline_access"] });
    }

    this.authorizations.push(request.headers.authorization);
    if (this.options.isValidToken) {
      const token = request.headers.authorization?.replace(/^Bearer /, "");
      if (!this.options.isValidToken(token)) {
        response.writeHead(401, {
          "www-authenticate": advertise === "challenge"
            ? `Bearer error="invalid_token", resource_metadata="${this.origin}/.well-known/oauth-protected-resource/mcp"`
            : `Bearer error="invalid_token"`,
        }).end();
        return;
      }
    }

    if (url.pathname === "/mcp/_discover") {
      if (!this.options.discover) {
        response.writeHead(404).end();
        return;
      }

      return json(response, 200, { tools: Object.entries(HTTP_METHODS).map(([name, method]) => ({ name, http_method: method })) });
    }

    if (url.pathname === "/mcp/_explain" && this.options.undefinedPermissions) {
      return json(response, 200, { count: 0, decisions: [], undefined_permissions: this.options.undefinedPermissions });
    }

    if (url.pathname !== "/mcp") {
      response.writeHead(404).end();
      return;
    }

    const body = request.method === "POST" ? JSON.parse(await readBody(request)) as unknown : undefined;
    if (isInitializeRequest(body)) {
      this.initializeAttempts++;
      if (this.failInitializations > 0) {
        this.failInitializations--;
        response.socket?.destroy();
        return;
      }
    }

    const message = body as { method?: unknown; id?: unknown } | undefined;
    if (this.failToolsList && message?.method === "tools/list") {
      return json(response, 200, { jsonrpc: "2.0", id: message.id, error: { code: -32603, message: "An internal error occurred." } });
    }
    const sessionId = request.headers["mcp-session-id"] as string | undefined;
    let session = sessionId ? this.sessions.get(sessionId) : undefined;
    if (sessionId && !session) {
      return json(response, 404, { jsonrpc: "2.0", error: { code: -32001, message: "Session not found" }, id: null });
    }

    if (session && sessionId && isToolCall(body, "Book_CrashMidStream")) {
      // A host that started streaming the answer, then died: headers and a first event, then the socket drops.
      this.count("Book_CrashMidStream");
      response.writeHead(200, { "content-type": "text/event-stream", "mcp-session-id": sessionId });
      response.write(": started\n\n");
      setTimeout(() => response.socket?.destroy(), 50);
      return;
    }

    if (!session) {
      if (!isInitializeRequest(body)) {
        return json(response, 400, { jsonrpc: "2.0", error: { code: -32000, message: "No session" }, id: null });
      }

      const mcp = this.createServer();
      const transport: StreamableHTTPServerTransport = new StreamableHTTPServerTransport({
        sessionIdGenerator: () => randomUUID(),
        onsessioninitialized: (id) => {
          this.sessions.set(id, { transport, mcp });
        },
      });
      await mcp.connect(transport);
      session = { transport, mcp };
    }

    await this.current.run(response, () => session!.transport.handleRequest(request, response, body));
  }

  private createServer(): McpServer {
    const mcp = new McpServer({ name: "Stub.Library", version: "1.0.0" }, { instructions: "Stub library host.", capabilities: { tools: { listChanged: true } } });
    const text = (value: string) => ({ content: [{ type: "text" as const, text: value }] });

    mcp.registerTool("Book_Get", { description: "Get a book." }, async () => {
      this.count("Book_Get");
      return text("Foundation");
    });
    mcp.registerTool("Book_List", { description: "List books (an ABP ListAsync: a POST that reads)." }, async () => {
      this.count("Book_List");
      return text("[]");
    });
    mcp.registerTool("Book_Delete", { description: "Delete a book." }, async () => {
      this.count("Book_Delete");
      return text("deleted");
    });
    mcp.registerTool("Book_Crash", { description: "Runs, then the connection drops." }, async () => {
      this.count("Book_Crash");
      this.current.getStore()?.socket?.destroy();
      return text("never delivered");
    });
    mcp.registerTool("Book_CrashMidStream", { description: "Starts answering, then the connection drops (handled in the HTTP layer)." }, async () => text("unreachable"));
    mcp.registerTool("Book_Slow", { description: "Runs until cancelled." }, async (extra) => {
      this.count("Book_Slow");
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, 10_000);
        extra.signal.addEventListener("abort", () => {
          clearTimeout(timer);
          resolve();
        });
      });
      return text("slow done");
    });
    mcp.registerTool("Library_Overview", { description: "A dynamic tool with no HTTP method." }, async () => {
      this.count("Library_Overview");
      return { isError: true, content: [{ type: "text" as const, text: "[FORBIDDEN] Missing permission: Library.Overview" }] };
    });

    return mcp;
  }
}

function isToolCall(body: unknown, tool: string): boolean {
  const message = body as { method?: unknown; params?: { name?: unknown } } | undefined;
  return message?.method === "tools/call" && message.params?.name === tool;
}
