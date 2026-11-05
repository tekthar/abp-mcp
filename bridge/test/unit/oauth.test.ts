import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { describe, it } from "node:test";
import { OAuthError } from "../../src/auth/errors.js";
import { chooseScope, parseWwwAuthenticate, requestToken } from "../../src/auth/oauth.js";
import { createPkce } from "../../src/auth/pkce.js";
import { Redactor } from "../../src/redact.js";

describe("WWW-Authenticate parsing", () => {
  it("reads quoted and bare parameters", () => {
    const [challenge] = parseWwwAuthenticate(`Bearer realm="api", error="invalid_token", resource_metadata="https://api.example.com/.well-known/oauth-protected-resource/mcp"`);
    assert.equal(challenge?.scheme, "Bearer");
    assert.equal(challenge?.params.resource_metadata, "https://api.example.com/.well-known/oauth-protected-resource/mcp");
    assert.equal(challenge?.params.error, "invalid_token");
  });

  it("separates several challenges and keeps commas inside quotes", () => {
    const challenges = parseWwwAuthenticate(`Basic realm="a, b", Bearer scope="x y", error=invalid_token`);
    assert.deepEqual(challenges.map((c) => c.scheme), ["Basic", "Bearer"]);
    assert.equal(challenges[0]?.params.realm, "a, b");
    assert.equal(challenges[1]?.params.scope, "x y");
    assert.equal(challenges[1]?.params.error, "invalid_token");
  });

  it("handles a bare scheme", () => {
    assert.deepEqual(parseWwwAuthenticate("Bearer"), [{ scheme: "Bearer", params: {} }]);
  });
});

describe("scope choice", () => {
  const server = { issuer: "https://auth", tokenEndpoint: "https://auth/token", scopesSupported: ["openid", "offline_access"] };

  it("prefers the profile's scope", () => {
    assert.equal(chooseScope("openid MyApp", { server, resourceScopes: ["Other"] }), "openid MyApp");
  });

  it("uses the host's scopes and adds offline_access when the server supports it", () => {
    assert.equal(chooseScope(undefined, { server, resourceScopes: ["MyApp"] }), "MyApp offline_access");
    assert.equal(chooseScope(undefined, { server: { ...server, scopesSupported: [] }, resourceScopes: ["MyApp"] }), "MyApp");
    assert.equal(chooseScope(undefined, { server }), undefined);
  });
});

describe("token requests", () => {
  it("returns the tokens, registers them for redaction, and computes expiry", async () => {
    const redactor = new Redactor();
    let body = "";
    const fetchImpl = async (_url: string | URL, init?: RequestInit) => {
      body = String(init?.body);
      return Response.json({ access_token: "access-123456", refresh_token: "refresh-123456", expires_in: 60 });
    };

    const tokens = await requestToken(fetchImpl, "https://auth/token", { grant_type: "password", password: "pw-123456" }, redactor, () => 1_000);
    assert.equal(tokens.accessToken, "access-123456");
    assert.equal(tokens.refreshToken, "refresh-123456");
    assert.equal(tokens.expiresAt, 61_000);
    assert.match(body, /grant_type=password/);
    assert.equal(redactor.redact("access-123456 refresh-123456 pw-123456"), "[REDACTED] [REDACTED] [REDACTED]");
  });

  it("raises the server's OAuth error", async () => {
    const fetchImpl = async () => Response.json({ error: "invalid_grant", error_description: "bad password" }, { status: 400 });
    await assert.rejects(requestToken(fetchImpl, "https://auth/token", {}, new Redactor()), (error: unknown) => {
      assert.ok(error instanceof OAuthError);
      assert.equal(error.error, "invalid_grant");
      assert.equal(error.isCredentialRejection, true);
      return true;
    });
  });
});

describe("PKCE", () => {
  it("derives the S256 challenge from a high-entropy verifier", () => {
    const pkce = createPkce();
    assert.ok(pkce.verifier.length >= 43);
    assert.equal(pkce.challenge, createHash("sha256").update(pkce.verifier).digest("base64url"));
    assert.notEqual(createPkce().state, pkce.state);
  });
});
