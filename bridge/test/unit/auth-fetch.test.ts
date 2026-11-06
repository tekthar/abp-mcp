import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { TokenManager } from "../../src/auth/token-manager.js";
import { createAuthenticatedFetch } from "../../src/upstream/auth-fetch.js";

/** A token source that hands out a new token after each invalidation. */
function rotatingTokens(): { tokens: TokenManager; invalidated: string[] } {
  let generation = 1;
  const invalidated: string[] = [];
  const tokens = {
    getAccessToken: async () => `token-${generation}`,
    invalidate: (token: string) => {
      invalidated.push(token);
      generation++;
    },
  } as unknown as TokenManager;
  return { tokens, invalidated };
}

describe("authenticated fetch", () => {
  it("renews the token after a 401 and sends the request once more", async () => {
    const { tokens } = rotatingTokens();
    const seen: (string | null)[] = [];
    const fetch = createAuthenticatedFetch({
      tokens, interactive: false,
      baseFetch: async (_url, init) => {
        const authorization = new Headers(init?.headers).get("authorization");
        seen.push(authorization);
        return new Response(null, { status: authorization === "Bearer token-1" ? 401 : 200 });
      },
    });

    assert.equal((await fetch("http://host/mcp", { method: "POST", body: "{}" })).status, 200);
    assert.deepEqual(seen, ["Bearer token-1", "Bearer token-2"]);
  });

  it("does not retry a second time after a second 401 (#107)", async () => {
    const { tokens, invalidated } = rotatingTokens();
    let sent = 0;
    const fetch = createAuthenticatedFetch({
      tokens, interactive: false,
      baseFetch: async () => {
        sent++;
        return new Response(null, { status: 401 });
      },
    });

    const response = await fetch("http://host/mcp", { method: "POST", body: "{}" });
    assert.equal(response.status, 401, "the second 401 reaches the caller");
    assert.equal(sent, 2, "one original request and exactly one retry");
    assert.deepEqual(invalidated, ["token-1"], "the renewed token is not invalidated and renewed again");
  });

  it("does not retry when renewal returns the same token", async () => {
    const tokens = { getAccessToken: async () => "same", invalidate: () => undefined } as unknown as TokenManager;
    let sent = 0;
    const fetch = createAuthenticatedFetch({
      tokens, interactive: false,
      baseFetch: async () => {
        sent++;
        return new Response(null, { status: 401 });
      },
    });

    assert.equal((await fetch("http://host/mcp")).status, 401);
    assert.equal(sent, 1);
  });
});
