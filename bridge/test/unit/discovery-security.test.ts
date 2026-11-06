import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { browserSignIn } from "../../src/auth/browser.js";
import { discoverSignIn, fetchAuthorizationServer, IssuerMismatchError, requestToken } from "../../src/auth/oauth.js";
import { InsecureUrlError, resourceCovers, urlPolicyViolation } from "../../src/auth/url-policy.js";
import { Logger } from "../../src/log.js";
import { Redactor } from "../../src/redact.js";

// GHSA-834p-c9gj-q699: URLs discovered from the host or its authorization server were used without the
// https rule the profile's URLs follow, and the resource and issuer were never compared.

const MCP = "https://api.example.com/mcp";
const PRM = "https://api.example.com/.well-known/oauth-protected-resource/mcp";
const ISSUER = "https://auth.example.com";

interface Fixture {
  challenge?: string;
  resourceMetadata?: Record<string, unknown>;
  serverMetadata?: Record<string, unknown>;
}

function fakeFetch(fixture: Fixture = {}) {
  const requested: { url: string; method: string }[] = [];
  const fetchImpl = async (input: string | URL, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    requested.push({ url, method: init?.method ?? "GET" });
    if (url === MCP) {
      return new Response(null, {
        status: 401,
        headers: { "www-authenticate": `Bearer resource_metadata="${fixture.challenge ?? PRM}"` },
      });
    }

    if (url === (fixture.challenge ?? PRM)) {
      return Response.json(fixture.resourceMetadata ?? { resource: MCP, authorization_servers: [ISSUER] });
    }

    if (url.endsWith("/.well-known/openid-configuration") || url.includes("/.well-known/oauth-authorization-server")) {
      return Response.json(fixture.serverMetadata ?? {
        issuer: ISSUER,
        token_endpoint: `${ISSUER}/connect/token`,
        authorization_endpoint: `${ISSUER}/connect/authorize`,
        revocation_endpoint: `${ISSUER}/connect/revocation`,
      });
    }

    return new Response(null, { status: 404 });
  };
  return { fetchImpl, requested };
}

function serverMetadata(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    issuer: ISSUER,
    token_endpoint: `${ISSUER}/connect/token`,
    authorization_endpoint: `${ISSUER}/connect/authorize`,
    revocation_endpoint: `${ISSUER}/connect/revocation`,
    ...overrides,
  };
}

describe("URL policy", () => {
  it("allows https anywhere and http only on loopback", () => {
    assert.equal(urlPolicyViolation("https://auth.example.com/token"), undefined);
    assert.equal(urlPolicyViolation("http://localhost:5000/token"), undefined);
    assert.equal(urlPolicyViolation("http://127.0.0.1:5000/token"), undefined);
    assert.equal(urlPolicyViolation("http://[::1]:5000/token"), undefined);
    for (const bad of ["http://auth.example.com/token", "http://localhost.evil.com/", "file:///C:/Windows/System32/calc.exe",
      "file://host/share/x.exe", "ms-settings:", "javascript:alert(1)", "not a url"]) {
      assert.ok(urlPolicyViolation(bad), bad);
    }
  });

  it("matches a resource against the requested URL by origin and path", () => {
    assert.ok(resourceCovers("https://api.example.com/mcp", "https://api.example.com/mcp"));
    assert.ok(resourceCovers("https://API.example.com:443/mcp/", "https://api.example.com/mcp"));
    assert.ok(resourceCovers("https://api.example.com", "https://api.example.com/mcp"));
    assert.ok(!resourceCovers("https://other.example.com/mcp", "https://api.example.com/mcp"));
    assert.ok(!resourceCovers("https://api.example.com/mcp-other", "https://api.example.com/mcp"));
    assert.ok(!resourceCovers("https://api.example.com/other", "https://api.example.com/mcp"));
    assert.ok(!resourceCovers("http://api.example.com/mcp", "https://api.example.com/mcp"));
  });
});

describe("discovery refuses insecure or mismatched metadata", () => {
  it("accepts well-formed https metadata", async () => {
    const { fetchImpl } = fakeFetch();
    const target = await discoverSignIn(fetchImpl, MCP);
    assert.equal(target.server.tokenEndpoint, `${ISSUER}/connect/token`);
    assert.equal(target.resource, MCP);
  });

  it("refuses an http resource_metadata URL from the 401 challenge", async () => {
    const { fetchImpl, requested } = fakeFetch({ challenge: "http://evil.example.com/prm" });
    await assert.rejects(discoverSignIn(fetchImpl, MCP), (error: unknown) =>
      error instanceof InsecureUrlError && /resource_metadata/.test(error.message));
    assert.ok(!requested.some((r) => new URL(r.url).origin === "http://evil.example.com"), "the insecure URL was never fetched");
  });

  it("refuses an http authorization server", async () => {
    const { fetchImpl, requested } = fakeFetch({ resourceMetadata: { resource: MCP, authorization_servers: ["http://auth.example.com"] } });
    await assert.rejects(discoverSignIn(fetchImpl, MCP), (error: unknown) =>
      error instanceof InsecureUrlError && /authorization_servers\[0\]/.test(error.message));
    assert.ok(!requested.some((r) => new URL(r.url).origin === "http://auth.example.com"));
  });

  for (const field of ["token_endpoint", "authorization_endpoint", "revocation_endpoint"]) {
    it(`refuses an http ${field}`, async () => {
      const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ [field]: "http://auth.example.com/x" }) });
      await assert.rejects(discoverSignIn(fetchImpl, MCP), (error: unknown) =>
        error instanceof InsecureUrlError && error.message.includes(field));
    });

    it(`refuses an http ${field} from a pinned authority too`, async () => {
      const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ [field]: "http://auth.example.com/x" }) });
      await assert.rejects(discoverSignIn(fetchImpl, MCP, ISSUER), InsecureUrlError);
    });
  }

  for (const scheme of ["file:///C:/Windows/System32/calc.exe", "file://attacker/share/run.exe", "my-handler://run?x=1"]) {
    it(`refuses an authorization_endpoint of ${scheme.split(":")[0]}:`, async () => {
      const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ authorization_endpoint: scheme }) });
      await assert.rejects(discoverSignIn(fetchImpl, MCP), InsecureUrlError);
    });
  }

  it("refuses a resource that does not match the MCP URL", async () => {
    const { fetchImpl, requested } = fakeFetch({ resourceMetadata: { resource: "https://other.example.com/mcp", authorization_servers: [ISSUER] } });
    await assert.rejects(discoverSignIn(fetchImpl, MCP), /RFC 9728 section 3\.3/);
    assert.ok(!requested.some((r) => new URL(r.url).origin === new URL(ISSUER).origin), "the authorization server was never contacted");
  });

  it("refuses resource metadata without a resource", async () => {
    const { fetchImpl } = fakeFetch({ resourceMetadata: { authorization_servers: [ISSUER] } });
    await assert.rejects(discoverSignIn(fetchImpl, MCP), /has no resource/);
  });

  it("refuses server metadata whose issuer differs from the one it was fetched for", async () => {
    const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ issuer: "https://evil.example.com" }) });
    await assert.rejects(fetchAuthorizationServer(fetchImpl, ISSUER), IssuerMismatchError);
  });

  it("refuses server metadata without an issuer", async () => {
    const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ issuer: undefined }) });
    await assert.rejects(fetchAuthorizationServer(fetchImpl, ISSUER), IssuerMismatchError);
  });

  it("treats an issuer with a trailing slash as the same issuer", async () => {
    const { fetchImpl } = fakeFetch({ serverMetadata: serverMetadata({ issuer: `${ISSUER}/` }) });
    const server = await fetchAuthorizationServer(fetchImpl, ISSUER);
    assert.equal(server.tokenEndpoint, `${ISSUER}/connect/token`);
  });
});

describe("credentials never go to an insecure endpoint", () => {
  it("requestToken refuses an http token endpoint before sending anything", async () => {
    let called = false;
    const fetchImpl = async () => {
      called = true;
      return Response.json({ access_token: "x" });
    };
    await assert.rejects(
      requestToken(fetchImpl, "http://auth.example.com/token", { grant_type: "password", password: "pw-123456" }, new Redactor()),
      InsecureUrlError);
    assert.equal(called, false);
  });

  it("requestToken does not follow redirects", async () => {
    let redirect: RequestInit["redirect"];
    const fetchImpl = async (_url: string | URL, init?: RequestInit) => {
      redirect = init?.redirect;
      return Response.json({ access_token: "access-123456" });
    };
    await requestToken(fetchImpl, "https://auth.example.com/token", { grant_type: "client_credentials" }, new Redactor());
    assert.equal(redirect, "error");
  });
});

describe("the browser opener only gets https", () => {
  for (const endpoint of ["file:///C:/Windows/System32/calc.exe", "file://attacker/share/run.exe", "my-handler://run", "http://auth.example.com/authorize"]) {
    it(`never opens ${endpoint}`, async () => {
      const opened: string[] = [];
      await assert.rejects(browserSignIn({
        authorizationEndpoint: endpoint,
        clientId: "MyApp_Mcp",
        ports: [0],
        redirectHost: "127.0.0.1",
        profileName: "p",
        openUrl: (url) => {
          opened.push(url);
        },
        log: new Logger({ redactor: new Redactor(), sink: () => undefined }),
        timeoutMs: 1_000,
      }), InsecureUrlError);
      assert.deepEqual(opened, []);
    });
  }
});

describe("openInBrowser", () => {
  it("throws before launching any OS handler for a non-https URL", async () => {
    const { openInBrowser } = await import("../../src/auth/browser.js");
    assert.throws(() => openInBrowser("file:///etc/passwd"), InsecureUrlError);
    assert.throws(() => openInBrowser("my-handler://run"), InsecureUrlError);
  });
});

describe("token requests mask short credentials", () => {
  it("registers a short password and client secret as credentials", async () => {
    const redactor = new Redactor();
    const fetchImpl = async () => Response.json({ access_token: "access-123456" });
    await requestToken(fetchImpl, "https://auth.example.com/token", { grant_type: "password", password: "pw1", client_secret: "cs2" }, redactor);
    assert.equal(redactor.redact("got pw1 and cs2"), "got [REDACTED] and [REDACTED]");
  });
});
