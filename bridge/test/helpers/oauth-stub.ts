import { createHash, randomBytes } from "node:crypto";
import http from "node:http";
import type { AddressInfo } from "node:net";

export interface OAuthStubOptions {
  readonly username?: string;
  readonly password?: string;
  readonly clientSecret?: string;
  readonly accessTokenLifetimeSeconds?: number;
}

interface IssuedCode {
  readonly challenge: string;
  readonly redirectUri: string;
  readonly scope: string;
}

/**
 * A small OpenIddict-like authorization server: discovery, authorize (auto-consent), token (code + PKCE,
 * refresh with rotation, password, client credentials) and revocation. Faults can be injected per test.
 */
export class OAuthStub {
  private server?: http.Server;
  private readonly codes = new Map<string, IssuedCode>();
  private readonly accessTokens = new Map<string, number>();
  private readonly refreshTokens = new Set<string>();

  /** Every token request's form fields, in order. */
  readonly tokenRequests: Record<string, string>[] = [];
  /** Every token and code this server handed out, for checking that none leaks into a log. */
  readonly issued: string[] = [];
  /** Every authorize request's query, in order. */
  readonly authorizeRequests: Record<string, string>[] = [];
  /** Return a state that does not match the request's. */
  tamperState = false;
  /** Omit offline_access handling: never issue refresh tokens. */
  noRefreshTokens = false;

  constructor(readonly options: OAuthStubOptions = {}) {}

  issuer = "";

  async start(): Promise<this> {
    this.server = http.createServer((request, response) => void this.handle(request, response));
    await new Promise<void>((resolve) => this.server!.listen(0, "127.0.0.1", resolve));
    this.issuer = `http://127.0.0.1:${(this.server.address() as AddressInfo).port}`;
    return this;
  }

  async stop(): Promise<void> {
    this.server?.closeAllConnections();
    await new Promise<void>((resolve) => this.server?.close(() => resolve()) ?? resolve());
  }

  /** True for an access token this server issued that has not expired or been revoked. */
  isValid(token: string | undefined): boolean {
    const expiresAt = token ? this.accessTokens.get(token) : undefined;
    return expiresAt !== undefined && expiresAt > Date.now();
  }

  /** Invalidate every access token, as if they all expired. */
  expireAccessTokens(): void {
    this.accessTokens.clear();
  }

  /** Invalidate every refresh token, as if they were revoked. */
  revokeRefreshTokens(): void {
    this.refreshTokens.clear();
  }

  get grantTypes(): string[] {
    return this.tokenRequests.map((request) => request.grant_type ?? "");
  }

  private async handle(request: http.IncomingMessage, response: http.ServerResponse): Promise<void> {
    const url = new URL(request.url ?? "/", this.issuer);
    if (url.pathname === "/.well-known/openid-configuration") {
      return json(response, 200, {
        issuer: this.issuer,
        authorization_endpoint: `${this.issuer}/connect/authorize`,
        token_endpoint: `${this.issuer}/connect/token`,
        revocation_endpoint: `${this.issuer}/connect/revocation`,
        scopes_supported: ["openid", "profile", "offline_access", "Library"],
      });
    }

    if (url.pathname === "/connect/authorize") {
      const query = Object.fromEntries(url.searchParams);
      this.authorizeRequests.push(query);
      if (query.code_challenge_method !== "S256" || !query.code_challenge || !query.redirect_uri?.startsWith("http://127.0.0.1:")) {
        return json(response, 400, { error: "invalid_request" });
      }

      const code = randomBytes(16).toString("hex");
      this.issued.push(code);
      this.codes.set(code, { challenge: query.code_challenge, redirectUri: query.redirect_uri, scope: query.scope ?? "" });
      const redirect = new URL(query.redirect_uri);
      redirect.searchParams.set("code", code);
      redirect.searchParams.set("state", this.tamperState ? "not-the-state" : query.state ?? "");
      response.writeHead(302, { location: redirect.toString() }).end();
      return;
    }

    if (url.pathname === "/connect/token" && request.method === "POST") {
      const form = Object.fromEntries(new URLSearchParams(await readBody(request)));
      this.tokenRequests.push(form);
      return this.token(form, response);
    }

    if (url.pathname === "/connect/revocation" && request.method === "POST") {
      const form = Object.fromEntries(new URLSearchParams(await readBody(request)));
      this.refreshTokens.delete(form.token ?? "");
      response.writeHead(200).end();
      return;
    }

    response.writeHead(404).end();
  }

  private token(form: Record<string, string>, response: http.ServerResponse): void {
    switch (form.grant_type) {
      case "authorization_code": {
        const issued = this.codes.get(form.code ?? "");
        this.codes.delete(form.code ?? "");
        const challenge = createHash("sha256").update(form.code_verifier ?? "").digest("base64url");
        if (!issued || issued.challenge !== challenge || issued.redirectUri !== form.redirect_uri) {
          return json(response, 400, { error: "invalid_grant", error_description: "The code or verifier is invalid." });
        }

        return this.issue(response, issued.scope);
      }

      case "refresh_token":
        if (!this.refreshTokens.delete(form.refresh_token ?? "")) {
          return json(response, 400, { error: "invalid_grant", error_description: "The refresh token is no longer valid." });
        }

        return this.issue(response, "offline_access");

      case "password":
        if (form.username !== (this.options.username ?? "alice") || form.password !== (this.options.password ?? "correct horse")) {
          return json(response, 400, { error: "invalid_grant", error_description: "Invalid username or password." });
        }

        return this.issue(response, form.scope ?? "");

      case "client_credentials":
        if (form.client_secret !== (this.options.clientSecret ?? "s3cret-client")) {
          return json(response, 401, { error: "invalid_client", error_description: "Invalid client secret." });
        }

        return this.issue(response, "", false);

      default:
        return json(response, 400, { error: "unsupported_grant_type" });
    }
  }

  private issue(response: http.ServerResponse, scope: string, allowRefresh = true): void {
    const lifetime = this.options.accessTokenLifetimeSeconds ?? 3600;
    const accessToken = `at-${randomBytes(12).toString("hex")}`;
    this.issued.push(accessToken);
    this.accessTokens.set(accessToken, Date.now() + lifetime * 1000);
    const body: Record<string, unknown> = { access_token: accessToken, token_type: "Bearer", expires_in: lifetime };
    if (allowRefresh && !this.noRefreshTokens && scope.split(" ").includes("offline_access")) {
      const refreshToken = `rt-${randomBytes(12).toString("hex")}`;
      this.issued.push(refreshToken);
      this.refreshTokens.add(refreshToken);
      body.refresh_token = refreshToken;
    }

    json(response, 200, body);
  }
}

/** A browser stand-in: follows the authorize redirect to the bridge's loopback callback. */
export async function autoConsent(url: string): Promise<void> {
  const authorize = await fetch(url, { redirect: "manual" });
  const location = authorize.headers.get("location");
  if (!location) {
    throw new Error(`The authorize endpoint did not redirect (${authorize.status}).`);
  }

  await (await fetch(location)).text();
}

export function json(response: http.ServerResponse, status: number, body: unknown): void {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(body));
}

export async function readBody(request: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(chunk as Buffer);
  }

  return Buffer.concat(chunks).toString("utf8");
}
