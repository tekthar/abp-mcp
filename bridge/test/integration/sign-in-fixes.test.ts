import assert from "node:assert/strict";
import path from "node:path";
import { after, afterEach, before, describe, it } from "node:test";
import { SignInBreaker } from "../../src/auth/breaker.js";
import { HostRejectedTokenError, ScopeRequiredError } from "../../src/auth/errors.js";
import { TokenManager } from "../../src/auth/token-manager.js";
import { CredentialVault, FileSecretStore, type SecretKind, type SecretStore } from "../../src/auth/vault.js";
import { addProfile } from "../../src/commands/profiles.js";
import { doctor } from "../../src/commands/doctor.js";
import { login } from "../../src/commands/login.js";
import type { Io } from "../../src/io.js";
import { Logger } from "../../src/log.js";
import { parseProfile, readProfilesDocument } from "../../src/profiles.js";
import { Redactor } from "../../src/redact.js";
import type { Runtime } from "../../src/runtime.js";
import { Upstream } from "../../src/upstream/upstream.js";
import { freePort, MemoryKeychain, tempDir } from "../helpers/harness.js";
import { McpStub, type McpStubOptions } from "../helpers/mcp-stub.js";
import { autoConsent, OAuthStub } from "../helpers/oauth-stub.js";

/** Windows Credential Manager through @napi-rs/keyring: values over 1280 characters (2560 bytes as UTF-16) fail. */
class WindowsLikeKeychain extends MemoryKeychain {
  override readonly description: string = "a Windows-like keychain";

  override async set(profile: string, kind: SecretKind, value: string): Promise<void> {
    if (value.length > 1280) {
      throw new Error("Value of 'password encoded as UTF-16' is longer than the platform limit of 2560 chars");
    }

    await super.set(profile, kind, value);
  }
}

/** A store that cannot write at all, for the case where neither store keeps the token. */
class BrokenStore implements SecretStore {
  readonly description = "a broken store";
  async get(): Promise<string | null> {
    return null;
  }

  async set(): Promise<void> {
    throw new Error("disk full");
  }

  async delete(): Promise<void> {
    return undefined;
  }
}

function recordingIo(answers: string[] = []): Io & { lines: string[]; prompts: string[] } {
  const lines: string[] = [];
  const prompts: string[] = [];
  return {
    lines,
    prompts,
    out: (line) => lines.push(line),
    err: (line) => lines.push(line),
    prompt: async (question) => {
      prompts.push(question);
      return answers.shift() ?? "";
    },
    promptHidden: async () => {
      throw new Error("unexpected hidden prompt");
    },
  };
}

describe("refresh-token storage (#116) and scope discovery (#117)", () => {
  const oauth = new OAuthStub();
  const temp = tempDir();
  let host: McpStub | undefined;
  let counter = 0;

  before(async () => {
    await oauth.start();
  });

  afterEach(async () => {
    await host?.stop();
    host = undefined;
    oauth.refreshTokenLength = 0;
    oauth.noRefreshTokens = false;
  });

  after(async () => {
    await oauth.stop();
    temp.cleanup();
  });

  async function startHost(options: Partial<McpStubOptions> = {}): Promise<McpStub> {
    host = await new McpStub({ isValidToken: (token) => oauth.isValid(token), issuer: oauth.issuer, discover: true, ...options }).start();
    return host;
  }

  function runtimeFor(spec: Record<string, unknown>, vault: CredentialVault, env: NodeJS.ProcessEnv = {}, opened: string[] = []): Runtime {
    const name = `p${++counter}`;
    const redactor = new Redactor();
    const log = new Logger({ redactor, sink: () => undefined });
    const breaker = SignInBreaker.forProfile(temp.dir, name);
    const profile = parseProfile(name, spec);
    const tokens = new TokenManager({
      profile, vault, breaker, log, redactor, env, browserTimeoutMs: 5_000,
      openUrl: async (url) => {
        opened.push(url);
        await autoConsent(url);
      },
    });
    return { profile, env, version: "test", redactor, log, vault, breaker, tokens, stateDir: temp.dir };
  }

  function fileStore(): FileSecretStore {
    return new FileSecretStore(path.join(temp.dir, `credentials-${++counter}`));
  }

  describe("#116: a refresh token the keychain refuses", () => {
    it("goes to the file store, and replaces a stale keychain entry", async () => {
      const keychain = new WindowsLikeKeychain();
      const file = fileStore();
      const vault = new CredentialVault(keychain, file);
      await keychain.set("p", "refresh_token", "rt-old");

      const long = "rt-".padEnd(3000, "x");
      const stored = await vault.setRefreshToken("p", long);

      assert.equal(stored.store, file.description);
      assert.ok(stored.keychainRefused instanceof Error);
      assert.equal(await keychain.get("p", "refresh_token"), null, "the stale keychain entry is gone");
      assert.equal(await vault.getRefreshToken("p"), long);
      assert.equal(await vault.refreshTokenStoreFor("p"), file.description);
    });

    it("still uses the keychain for a token it accepts", async () => {
      const keychain = new WindowsLikeKeychain();
      const vault = new CredentialVault(keychain, fileStore());
      const stored = await vault.setRefreshToken("p", "rt-short");
      assert.equal(stored.store, keychain.description);
      assert.equal(stored.keychainRefused, undefined);
      assert.equal(await vault.refreshTokenStoreFor("p"), keychain.description);
    });

    it("login keeps a long token in the file and says where", async () => {
      const mcp = await startHost();
      oauth.refreshTokenLength = 3000;
      const keychain = new WindowsLikeKeychain();
      const file = fileStore();
      const vault = new CredentialVault(keychain, file);
      const spec = { url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice" } };
      const env = { ABP_MCP_PASSWORD: "correct horse" };
      const io = recordingIo();
      const runtime = runtimeFor(spec, vault, env);

      await login(runtime, { save: false }, io);

      const output = io.lines.join("\n");
      assert.match(output, new RegExp(`kept in ${escape(file.description)} and renewed automatically`));
      assert.doesNotMatch(output, /issued no refresh token/);
      assert.equal((await file.get(runtime.profile.name, "refresh_token"))?.length, 3000, "the long token is in the file");
      assert.equal(await keychain.get(runtime.profile.name, "refresh_token"), null);
    });

    it("a second TokenManager on the same profile renews from the file-stored token", async () => {
      const mcp = await startHost();
      oauth.refreshTokenLength = 3000;
      const vault = new CredentialVault(new WindowsLikeKeychain(), fileStore());
      const spec = { url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice" } };
      const one = runtimeFor(spec, vault, { ABP_MCP_PASSWORD: "correct horse" });
      await one.tokens.getAccessToken({ interactive: false });

      // Same profile name, no password: only the stored refresh token can sign it in.
      const two = new TokenManager({
        profile: one.profile, vault, breaker: one.breaker, log: one.log, redactor: new Redactor(), env: {},
      });
      const refreshes = oauth.grantTypes.filter((type) => type === "refresh_token").length;
      const token = await two.getAccessToken({ interactive: false });
      assert.ok(oauth.isValid(token ?? undefined));
      assert.equal(oauth.grantTypes.filter((type) => type === "refresh_token").length, refreshes + 1);
    });

    it("login reports a token it could not store, not one the server did not send", async () => {
      const mcp = await startHost();
      const vault = new CredentialVault(undefined, new BrokenStore());
      const spec = { url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice" } };
      const io = recordingIo();

      await login(runtimeFor(spec, vault, { ABP_MCP_PASSWORD: "correct horse" }), { save: false }, io);

      const output = io.lines.join("\n");
      assert.match(output, /The server issued a refresh token, but it could not be stored \(disk full\)/);
      assert.doesNotMatch(output, /issued no refresh token/);
    });

    it("doctor names the store the profile's token is actually in", async () => {
      const mcp = await startHost();
      oauth.refreshTokenLength = 3000;
      const file = fileStore();
      const vault = new CredentialVault(new WindowsLikeKeychain(), file);
      const runtime = runtimeFor({ url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice" } }, vault, { ABP_MCP_PASSWORD: "correct horse" });
      const io = recordingIo();

      await doctor(runtime, { tools: false }, io);

      assert.match(io.lines.join("\n"), new RegExp(`refresh tokens are kept in ${escape(file.description)}`));
    });
  });

  describe("#117: auth.authority without auth.scope", () => {
    it("still sends the host's scopes, plus offline_access, and its resource", async () => {
      const mcp = await startHost();
      const opened: string[] = [];
      const runtime = runtimeFor(
        { url: mcp.url, auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [await freePort()] } },
        new CredentialVault(undefined, fileStore()), {}, opened);

      await runtime.tokens.signInWithBrowser();

      const authorize = oauth.authorizeRequests.at(-1)!;
      assert.equal(authorize.scope, "Library offline_access");
      assert.equal(authorize.resource, mcp.url);
      assert.equal(oauth.tokenRequests.at(-1)?.resource, mcp.url);
      assert.equal(opened.length, 1);
    });

    it("says when the pinned authority is not one the host lists", async () => {
      const mcp = await startHost({ issuer: "https://auth.other.example" });
      const runtime = runtimeFor(
        { url: mcp.url, auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer } },
        new CredentialVault(undefined, fileStore()));

      const target = await runtime.tokens.signInTarget();

      assert.equal(target.server.issuer, oauth.issuer, "the authority still picks the server");
      assert.equal(target.resource, mcp.url);
      assert.match((target.notes ?? []).join("\n"), /is not among the authorization servers the host lists \(https:\/\/auth\.other\.example\)/);
    });

    it("refuses to sign in with no scope at all, and never opens the browser", async () => {
      const mcp = await startHost({ resourceMetadata: "none" });
      const opened: string[] = [];
      const runtime = runtimeFor(
        { url: mcp.url, auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [await freePort()] } },
        new CredentialVault(undefined, fileStore()), {}, opened);

      await assert.rejects(runtime.tokens.signInWithBrowser(), (error: unknown) =>
        error instanceof ScopeRequiredError && /set auth\.scope|Set auth\.scope/.test(error.message));
      assert.equal(opened.length, 0);

      const io = recordingIo();
      await doctor(runtime, { tools: false }, io);
      assert.match(io.lines.join("\n"), /✗ Authorization server: .*no scope to request/);
    });

    it("reports a 401 for a token just issued as a rejected token, with one period", async () => {
      const mcp = await startHost({ isValidToken: () => false });
      oauth.noRefreshTokens = true;
      const io = recordingIo();
      const runtime = runtimeFor(
        { url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice", authority: oauth.issuer, scope: "openid Library" } },
        new CredentialVault(undefined, fileStore()), { ABP_MCP_PASSWORD: "correct horse" });

      await login(runtime, { save: false }, io);

      const output = io.lines.join("\n");
      assert.match(output, /listing tools failed: The host rejected the access token just issued for profile 'p\d+' \(HTTP 401: invalid_token\)/);
      assert.doesNotMatch(output, /is not signed in/);
      assert.doesNotMatch(output, /\.\./, "no doubled period");
    });

    it("also when renewal succeeds and the host rejects the renewed token", async () => {
      const mcp = await startHost({ isValidToken: () => false });
      const runtime = runtimeFor(
        { url: mcp.url, auth: { type: "password", clientId: "MyApp_App", username: "alice", authority: oauth.issuer, scope: "openid offline_access Library" } },
        new CredentialVault(undefined, fileStore()), { ABP_MCP_PASSWORD: "correct horse" });
      const upstream = new Upstream({ profile: runtime.profile, tokens: runtime.tokens, log: runtime.log, version: "test", interactive: false, onToolListChanged: () => undefined });
      try {
        await assert.rejects(upstream.listTools(), HostRejectedTokenError);
      } finally {
        await upstream.close();
      }
    });
  });

  describe("#117: profiles add", () => {
    it("does not ask for an authorization server when the host publishes its metadata", async () => {
      const mcp = await startHost();
      const profiles = path.join(temp.dir, `profiles-${++counter}.json`);
      const io = recordingIo(["MyApp_Mcp", ""]);

      await addProfile("web", { url: mcp.url, stage: "development", auth: "browser", interactive: true }, { ABP_MCP_PROFILES: profiles }, io);

      assert.ok(!io.prompts.some((question) => /Authorization server URL/.test(question)), io.prompts.join(" | "));
      assert.match(io.lines.join("\n"), /publishes its sign-in metadata/);
      assert.equal((readProfilesDocument(profiles).profiles.web as { auth?: { authority?: string } } | undefined)?.auth?.authority, undefined);
    });

    it("asks for it, saying why, when the host publishes none", async () => {
      const mcp = await startHost({ resourceMetadata: "none" });
      const profiles = path.join(temp.dir, `profiles-${++counter}.json`);
      const io = recordingIo(["MyApp_Mcp", oauth.issuer, "openid offline_access Library"]);

      await addProfile("web", { url: mcp.url, stage: "development", auth: "browser", interactive: true }, { ABP_MCP_PROFILES: profiles }, io);

      assert.ok(io.prompts.some((question) => /Authorization server URL \(the host publishes no OAuth metadata/.test(question)), io.prompts.join(" | "));
      assert.equal((readProfilesDocument(profiles).profiles.web as { auth?: { authority?: string } } | undefined)?.auth?.authority, oauth.issuer);
    });
  });
});

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
