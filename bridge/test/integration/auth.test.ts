import assert from "node:assert/strict";
import path from "node:path";
import { after, before, describe, it } from "node:test";
import { SignInBreaker } from "../../src/auth/breaker.js";
import type { OpenUrl } from "../../src/auth/browser.js";
import { BreakerOpenError, NotSignedInError, OAuthError } from "../../src/auth/errors.js";
import { TokenManager } from "../../src/auth/token-manager.js";
import { CredentialVault, FileSecretStore } from "../../src/auth/vault.js";
import { Logger } from "../../src/log.js";
import { parseProfile } from "../../src/profiles.js";
import { Redactor } from "../../src/redact.js";
import { freePort, MemoryKeychain, tempDir } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";
import { autoConsent, OAuthStub } from "../helpers/oauth-stub.js";

interface Setup {
  readonly tokens: TokenManager;
  readonly vault: CredentialVault;
  readonly breaker: SignInBreaker;
  readonly clock: { now: number };
  readonly opened: string[];
}

describe("sign-in", () => {
  const oauth = new OAuthStub();
  const host = new McpStub({ isValidToken: (token) => oauth.isValid(token), discover: true });
  const temp = tempDir();
  let counter = 0;
  let port = 0;

  before(async () => {
    port = await freePort();
    await oauth.start();
    await host.start();
    (host as unknown as { options: { issuer: string } }).options.issuer = oauth.issuer;
  });

  after(async () => {
    await host.stop();
    await oauth.stop();
    temp.cleanup();
  });

  function setup(spec: Record<string, unknown>, env: NodeJS.ProcessEnv = {}, openUrl?: OpenUrl): Setup {
    const name = `p${++counter}`;
    const clock = { now: Date.now() };
    const opened: string[] = [];
    const redactor = new Redactor();
    const log = new Logger({ redactor, sink: () => undefined });
    const vault = new CredentialVault(new MemoryKeychain(), new FileSecretStore(path.join(temp.dir, "credentials")));
    const breaker = SignInBreaker.forProfile(temp.dir, name, () => clock.now);
    const tokens = new TokenManager({
      profile: parseProfile(name, { url: host.url, ...spec }),
      vault, breaker, log, redactor, env, now: () => clock.now, browserTimeoutMs: 5_000,
      openUrl: async (url) => {
        opened.push(url);
        await (openUrl ?? autoConsent)(url);
      },
    });
    return { tokens, vault, breaker, clock, opened };
  }

  const passwordSpec = () => ({ auth: { type: "password", clientId: "MyApp_App", authority: oauth.issuer, scope: "openid offline_access Library", username: "alice" } });

  it("password: signs in, keeps the refresh token, and refreshes 60 s before expiry with rotation", async () => {
    const { tokens, vault, clock } = setup(passwordSpec());
    await tokens.signInWithPassword("correct horse", false);
    const first = await tokens.getAccessToken({ interactive: false });
    const storedBefore = await vault.getRefreshToken(`p${counter}`);
    assert.ok(storedBefore);

    clock.now += 3600_000 - 59_000;
    const renewed = await tokens.getAccessToken({ interactive: false });
    assert.notEqual(renewed, first);
    assert.equal(oauth.grantTypes.at(-1), "refresh_token");
    const storedAfter = await vault.getRefreshToken(`p${counter}`);
    assert.notEqual(storedAfter, storedBefore, "a rotated refresh token is stored again");
  });

  it("password: re-mints from ABP_MCP_PASSWORD when the refresh token was revoked", async () => {
    const { tokens, clock } = setup(passwordSpec(), { ABP_MCP_PASSWORD: "correct horse" });
    await tokens.getAccessToken({ interactive: false });
    oauth.revokeRefreshTokens();
    clock.now += 3600_000;
    const before = oauth.tokenRequests.length;

    assert.ok(await tokens.getAccessToken({ interactive: false }));
    assert.deepEqual(oauth.grantTypes.slice(before), ["refresh_token", "password"]);
  });

  it("password: one wrong password pauses sign-in instead of locking the account", async () => {
    const { tokens, breaker } = setup(passwordSpec(), { ABP_MCP_PASSWORD: "wrong" });
    await assert.rejects(tokens.getAccessToken({ interactive: false }), (error: unknown) => error instanceof OAuthError && error.error === "invalid_grant");
    const attempts = oauth.tokenRequests.length;

    for (let i = 0; i < 3; i++) {
      await assert.rejects(tokens.getAccessToken({ interactive: false }), BreakerOpenError);
    }

    assert.equal(oauth.tokenRequests.length, attempts, "no further attempt reached the server");
    assert.equal(breaker.status().open, true);
    assert.equal(await tokens.canSignInSilently(), false);
  });

  it("password: without a refresh token or a password, asks the user to log in", async () => {
    oauth.noRefreshTokens = true;
    try {
      const { tokens, clock } = setup(passwordSpec());
      await tokens.signInWithPassword("correct horse", false);
      clock.now += 3600_000;
      await assert.rejects(tokens.getAccessToken({ interactive: false }), (error: unknown) =>
        error instanceof NotSignedInError && /abp-mcp-bridge login/.test(error.message));
    } finally {
      oauth.noRefreshTokens = false;
    }
  });

  it("password: --save-password keeps it in the keychain for silent re-sign-in", async () => {
    const { tokens, vault } = setup(passwordSpec());
    await tokens.signInWithPassword("correct horse", true);
    assert.equal(await vault.getSecret(`p${counter}`, "password"), "correct horse");
    assert.equal(await tokens.canSignInSilently(), true);
  });

  it("client_credentials: uses ABP_MCP_CLIENT_SECRET, and a wrong secret trips the breaker", async () => {
    const spec = { auth: { type: "client_credentials", clientId: "Robot", authority: oauth.issuer, scope: "Library" } };
    assert.ok(await setup(spec, { ABP_MCP_CLIENT_SECRET: "s3cret-client" }).tokens.getAccessToken({ interactive: false }));

    const wrong = setup(spec, { ABP_MCP_CLIENT_SECRET: "nope" });
    await assert.rejects(wrong.tokens.getAccessToken({ interactive: false }), OAuthError);
    await assert.rejects(wrong.tokens.getAccessToken({ interactive: false }), BreakerOpenError);
  });

  it("browser: discovers the server from the host's 401, uses PKCE, and sends the resource", async () => {
    const { tokens, opened } = setup({ auth: { type: "browser", clientId: "MyApp_Mcp", callbackPorts: [port] } });
    const token = await tokens.getAccessToken({ interactive: true });
    assert.ok(oauth.isValid(token ?? undefined));
    assert.equal(opened.length, 1);

    const authorize = oauth.authorizeRequests.at(-1)!;
    assert.equal(authorize.code_challenge_method, "S256");
    assert.equal(authorize.resource, host.url, "RFC 8707 resource from the host's metadata");
    assert.equal(authorize.scope, "Library offline_access");
    const exchange = oauth.tokenRequests.at(-1)!;
    assert.equal(exchange.grant_type, "authorization_code");
    assert.equal(exchange.resource, host.url);
    assert.ok(exchange.code_verifier);
  });

  it("browser: rejects a response whose state does not match", async () => {
    oauth.tamperState = true;
    try {
      const { tokens } = setup({ auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [port] } });
      const exchanges = oauth.grantTypes.filter((type) => type === "authorization_code").length;
      await assert.rejects(tokens.getAccessToken({ interactive: true }), (error: unknown) => error instanceof OAuthError && error.error === "invalid_state");
      assert.equal(oauth.grantTypes.filter((type) => type === "authorization_code").length, exchanges, "the code was never exchanged");
    } finally {
      oauth.tamperState = false;
    }
  });

  it("browser: never opens a browser when not interactive", async () => {
    const { tokens, opened } = setup({ auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [port] } });
    await assert.rejects(tokens.getAccessToken({ interactive: false }), NotSignedInError);
    assert.equal(opened.length, 0);
  });

  it("concurrent requests share one sign-in", async () => {
    const { tokens, opened } = setup({ auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [port] } });
    const results = await Promise.all([1, 2, 3].map(() => tokens.getAccessToken({ interactive: true })));
    assert.equal(new Set(results).size, 1);
    assert.equal(opened.length, 1);
  });

  it("logout revokes and forgets the refresh token", async () => {
    const { tokens, vault } = setup(passwordSpec());
    await tokens.signInWithPassword("correct horse", true);
    await tokens.logout();
    assert.equal(await vault.getRefreshToken(`p${counter}`), null);
    assert.equal(await vault.getSecret(`p${counter}`, "password"), null);
  });

  it("token: reads ABP_MCP_TOKEN, and says where it looked when it is missing", async () => {
    assert.equal(await setup({ auth: { type: "token" } }, { ABP_MCP_TOKEN: "static-token-123" }).tokens.getAccessToken({ interactive: false }), "static-token-123");
    await assert.rejects(setup({ auth: { type: "token", tokenEnv: "MY_TOKEN" } }).tokens.getAccessToken({ interactive: false }), /MY_TOKEN/);
  });
});
