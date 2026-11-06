import type { CodeLimits } from "../../src/gateway/code-mode.js";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { CallToolResultSchema, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { SignInBreaker } from "../../src/auth/breaker.js";
import type { OpenUrl } from "../../src/auth/browser.js";
import type { FetchLike } from "../../src/auth/oauth.js";
import { TokenManager } from "../../src/auth/token-manager.js";
import { CredentialVault, FileSecretStore, type SecretKind, type SecretStore } from "../../src/auth/vault.js";
import { AuditLog } from "../../src/audit.js";
import { Bridge } from "../../src/bridge.js";
import { Logger } from "../../src/log.js";
import { parseProfile, type Profile } from "../../src/profiles.js";
import { Redactor } from "../../src/redact.js";
import { Upstream } from "../../src/upstream/upstream.js";

/** A port that is free right now, for the browser callback. */
export async function freePort(): Promise<number> {
  const net = await import("node:net");
  return await new Promise<number>((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as { port: number };
      server.close(() => resolve(port));
    });
  });
}

/** A throwaway directory, removed by the returned cleanup. */
export function tempDir(): { dir: string; cleanup: () => void } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "abp-mcp-bridge-test-"));
  return { dir, cleanup: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

/** An in-memory stand-in for the OS keychain. */
export class MemoryKeychain implements SecretStore {
  readonly description: string = "a test keychain";
  readonly values = new Map<string, string>();

  async get(profile: string, kind: SecretKind): Promise<string | null> {
    return this.values.get(`${profile}:${kind}`) ?? null;
  }

  async set(profile: string, kind: SecretKind, value: string): Promise<void> {
    this.values.set(`${profile}:${kind}`, value);
  }

  async delete(profile: string, kind: SecretKind): Promise<void> {
    this.values.delete(`${profile}:${kind}`);
  }
}

export interface HarnessOptions {
  readonly spec: Record<string, unknown>;
  readonly env?: NodeJS.ProcessEnv;
  readonly openUrl?: OpenUrl;
  readonly keychain?: SecretStore | null;
  /** `true` for an audit log at {@link Harness.auditFile}, or a log built from that path and the harness's logger. */
  readonly audit?: boolean | ((file: string, log: Logger) => AuditLog);
  readonly signInWaitMs?: number;
  readonly initializeWaitMs?: number;
  /** The fetch under the upstream's authentication, to observe or delay requests to the host. */
  readonly baseFetch?: FetchLike;
  /** Limits for `run_code` in gateway mode. */
  readonly codeLimits?: Partial<CodeLimits>;
}

/** A bridge wired like `runStdio`, with Claude's side as an in-memory MCP client. */
export class Harness {
  readonly lines: string[] = [];
  readonly redactor = new Redactor();
  readonly log: Logger;
  readonly profile: Profile;
  readonly vault: CredentialVault;
  readonly breaker: SignInBreaker;
  readonly tokens: TokenManager;
  readonly upstream: Upstream;
  readonly bridge: Bridge;
  readonly client = new Client({ name: "test-claude", version: "1.0.0" });
  readonly keychain: SecretStore | undefined;
  readonly toolListChanges: number[] = [];
  readonly auditFile: string;
  private readonly temp = tempDir();

  constructor(options: HarnessOptions) {
    this.log = new Logger({ redactor: this.redactor, debug: true, sink: (line) => this.lines.push(line), file: path.join(this.temp.dir, "bridge.log") });
    this.profile = parseProfile("test", options.spec);
    this.keychain = options.keychain === null ? undefined : options.keychain ?? new MemoryKeychain();
    this.vault = new CredentialVault(this.keychain, new FileSecretStore(path.join(this.temp.dir, "credentials")));
    this.breaker = SignInBreaker.forProfile(this.temp.dir, "test");
    this.tokens = new TokenManager({
      profile: this.profile, vault: this.vault, breaker: this.breaker, log: this.log, redactor: this.redactor,
      env: options.env ?? {}, ...(options.openUrl ? { openUrl: options.openUrl } : {}), browserTimeoutMs: 10_000,
    });
    const late: { bridge?: Bridge } = {};
    this.upstream = new Upstream({
      profile: this.profile, tokens: this.tokens, log: this.log, version: "0.0.0-test",
      interactive: this.profile.auth.type === "browser",
      onToolListChanged: () => late.bridge?.onUpstreamToolsChanged(),
      ...(options.baseFetch ? { baseFetch: options.baseFetch } : {}),
    });
    this.auditFile = path.join(this.temp.dir, "audit", "test.jsonl");
    const audit = typeof options.audit === "function"
      ? options.audit(this.auditFile, this.log)
      : options.audit ? new AuditLog(this.auditFile, "test", { log: this.log }) : undefined;
    this.bridge = new Bridge({
      profile: this.profile, version: "0.0.0-test", tokens: this.tokens, upstream: this.upstream, log: this.log,
      ...(audit ? { audit } : {}),
      ...(options.signInWaitMs !== undefined ? { signInWaitMs: options.signInWaitMs } : {}),
      ...(options.initializeWaitMs !== undefined ? { initializeWaitMs: options.initializeWaitMs } : {}),
      ...(options.codeLimits ? { codeLimits: options.codeLimits } : {}),
    });
    late.bridge = this.bridge;
  }

  get logFile(): string {
    return path.join(this.temp.dir, "bridge.log");
  }

  /** Connect Claude's side. The bridge's server is built first, as in production. */
  async connect(): Promise<this> {
    const [clientSide, serverSide] = InMemoryTransport.createLinkedPair();
    this.client.fallbackNotificationHandler = async (notification) => {
      if (notification.method === "notifications/tools/list_changed") {
        this.toolListChanges.push(Date.now());
      }
    };
    await this.bridge.start(serverSide);
    await this.client.connect(clientSide);
    return this;
  }

  async listToolNames(): Promise<string[]> {
    return (await this.client.listTools()).tools.map((tool) => tool.name).sort();
  }

  /** A raw tools/call, without the SDK's client-side output validation. */
  async call(name: string, args: Record<string, unknown> = {}, signal?: AbortSignal): Promise<CallToolResult> {
    return await this.client.request({ method: "tools/call", params: { name, arguments: args } }, CallToolResultSchema, signal ? { signal } : {}) as CallToolResult;
  }

  async close(): Promise<void> {
    await this.client.close().catch(() => undefined);
    await this.upstream.close();
    this.temp.cleanup();
  }
}

/** The text of a tool result's first content block. */
export function textOf(result: CallToolResult): string {
  const first = result.content[0];
  return first?.type === "text" ? first.text : "";
}

/** Wait until `condition` holds, or fail after `timeoutMs`. */
export async function waitFor(condition: () => boolean, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error("Timed out waiting for a condition.");
    }

    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}
