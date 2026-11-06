import assert from "node:assert/strict";
import path from "node:path";
import { after, before, describe, it } from "node:test";
import { SignInBreaker } from "../../src/auth/breaker.js";
import { NotSignedInError } from "../../src/auth/errors.js";
import type { FetchLike } from "../../src/auth/oauth.js";
import { TokenManager } from "../../src/auth/token-manager.js";
import { CredentialVault, FileSecretStore } from "../../src/auth/vault.js";
import { Logger } from "../../src/log.js";
import { parseProfile } from "../../src/profiles.js";
import { Redactor } from "../../src/redact.js";
import { freePort, MemoryKeychain, tempDir } from "../helpers/harness.js";
import { autoConsent, OAuthStub } from "../helpers/oauth-stub.js";

/** Two bridges on one profile (Claude Code and Claude Desktop), and a browser sign-in racing a probe. */
describe("races (#103)", () => {
  const oauth = new OAuthStub();
  const temp = tempDir();
  let counter = 0;
  let port = 0;

  before(async () => {
    port = await freePort();
    await oauth.start();
  });

  after(async () => {
    await oauth.stop();
    temp.cleanup();
  });

  const passwordAuth = () => ({ type: "password", clientId: "MyApp_App", authority: oauth.issuer, scope: "openid offline_access Library", username: "alice" });

  /** Token managers for one profile that share one vault, each with its own clock and fetch. */
  function bridges(auth: Record<string, unknown>, env: NodeJS.ProcessEnv = {}) {
    const name = `race${++counter}`;
    const vault = new CredentialVault(new MemoryKeychain(), new FileSecretStore(path.join(temp.dir, `credentials-${name}`)));
    const opened: string[] = [];
    const make = (options: { fetch?: FetchLike; clock?: { now: number } } = {}) => {
      const redactor = new Redactor();
      const clock = options.clock ?? { now: Date.now() };
      const tokens = new TokenManager({
        profile: parseProfile(name, { url: "http://127.0.0.1:1/mcp", auth }),
        vault, breaker: SignInBreaker.forProfile(temp.dir, name, () => clock.now), log: new Logger({ redactor, sink: () => undefined }),
        redactor, env, now: () => clock.now, browserTimeoutMs: 5_000,
        ...(options.fetch ? { fetch: options.fetch } : {}),
        openUrl: async (url) => {
          opened.push(url);
          await autoConsent(url);
        },
      });
      return { tokens, clock };
    };

    return { name, vault, opened, make };
  }

  it("a bridge holding a rotated-away refresh token uses the newer one instead of deleting it", async () => {
    const { name, vault, make } = bridges(passwordAuth());
    const a = make();
    await a.tokens.signInWithPassword("correct horse", false);
    const rt1 = await vault.getRefreshToken(name);
    assert.ok(rt1);

    // B reads RT1, and its refresh reaches the server only after A has rotated RT1 to RT2.
    let reached!: () => void;
    const bReached = new Promise<void>((resolve) => {
      reached = resolve;
    });
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let held = false;
    const b = make({
      fetch: async (url, init) => {
        if (!held && String(url).endsWith("/connect/token")) {
          held = true;
          reached();
          await gate;
        }

        return fetch(url, init);
      },
    });

    const bToken = b.tokens.getAccessToken({ interactive: false });
    await bReached;
    a.clock.now += 3600_000;
    await a.tokens.getAccessToken({ interactive: false });
    const rt2 = await vault.getRefreshToken(name);
    assert.ok(rt2 && rt2 !== rt1, "A rotated the refresh token");

    const before = oauth.tokenRequests.length;
    release();
    assert.ok(oauth.isValid((await bToken) ?? undefined), "B got a token");
    assert.deepEqual(oauth.grantTypes.slice(before), ["refresh_token", "refresh_token"], "RT1 rejected, then RT2 used; no password grant");

    const rt3 = await vault.getRefreshToken(name);
    assert.ok(rt3 && rt3 !== rt2, "B stored the token it rotated to");

    // A still signs in silently afterwards.
    a.clock.now += 3600_000;
    assert.ok(oauth.isValid((await a.tokens.getAccessToken({ interactive: false })) ?? undefined));
  });

  it("a rejected refresh token is deleted only while it is still the stored one", async () => {
    const { name, vault, make } = bridges(passwordAuth());
    await vault.setRefreshToken(name, "rt-revoked-1");

    // Another bridge stores a newer token while this one's rejected refresh is on the wire.
    let swapped = false;
    const b = make({
      fetch: async (url, init) => {
        const response = await fetch(url, init);
        if (!swapped && String(url).endsWith("/connect/token")) {
          swapped = true;
          await vault.setRefreshToken(name, "rt-newer-but-also-revoked");
        }

        return response;
      },
    });

    await assert.rejects(b.tokens.getAccessToken({ interactive: false }), NotSignedInError);
    // The newer token was tried once, rejected too, and then deleted: it was the stored one by then.
    assert.equal(await vault.getRefreshToken(name), null);

    await vault.setRefreshToken(name, "rt-newest");
    await vault.deleteRefreshTokenIf(name, "rt-revoked-1");
    assert.equal(await vault.getRefreshToken(name), "rt-newest", "compare-and-delete keeps a token it did not reject");
  });

  it("an interactive request during a non-interactive probe opens the browser instead of failing", async () => {
    const { opened, make } = bridges({ type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [port] });
    const { tokens } = make();

    const probe = tokens.getAccessToken({ interactive: false });
    const call = tokens.getAccessToken({ interactive: true });
    await assert.rejects(probe, NotSignedInError);
    assert.ok(oauth.isValid((await call) ?? undefined), "the interactive caller signed in");
    assert.equal(opened.length, 1);
  });

  it("several interactive requests behind a probe share one browser sign-in", async () => {
    const { opened, make } = bridges({ type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, callbackPorts: [port] });
    const { tokens } = make();

    const probe = tokens.getAccessToken({ interactive: false }).catch(() => undefined);
    const calls = [1, 2, 3].map(() => tokens.getAccessToken({ interactive: true }));
    await probe;
    assert.equal(new Set(await Promise.all(calls)).size, 1);
    assert.equal(opened.length, 1);
  });
});
