import { createHash, generateKeyPairSync, randomBytes, sign, type KeyObject } from "node:crypto";
import http from "node:http";
import type { AddressInfo } from "node:net";

export interface OAuthStubOptions {
  readonly username?: string;
  readonly password?: string;
  readonly clientSecret?: string;
  readonly accessTokenLifetimeSeconds?: number;
  /**
   * Named users, each with its own `sub`. The browser flow signs in the user named by `login_hint` (the first
   * user without one); the password grant checks the user's password. Without users, the stub has one user,
   * `username`/`password`, with no `sub`.
   */
  readonly users?: Readonly<Record<string, { readonly sub: string; readonly password?: string }>>;
  /**
   * Issue access tokens as RS256 JWTs for this audience, and publish the signing key at `jwks_uri`, so a real
   * resource server (JwtBearer) can validate them. Without it, access tokens are opaque.
   */
  readonly jwt?: { readonly audience: string };
}

interface IssuedCode {
  readonly challenge: string;
  readonly redirectUri: string;
  readonly scope: string;
  readonly user?: string;
}

interface IssuedRefreshToken {
  readonly scope: string;
  readonly user?: string;
}

/**
 * A small OpenIddict-like authorization server: discovery, authorize (auto-consent), token (code + PKCE,
 * refresh with rotation, password, client credentials) and revocation. Faults can be injected per test.
 */
export class OAuthStub {
  private server?: http.Server;
  private readonly codes = new Map<string, IssuedCode>();
  private readonly accessTokens = new Map<string, number>();
  private readonly refreshTokens = new Map<string, IssuedRefreshToken>();
  private signingKey?: { readonly privateKey: KeyObject; readonly jwk: Record<string, unknown> };

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
  /**
   * Pad refresh tokens to at least this many characters, like OpenIddict's encrypted self-contained tokens
   * (longer than Windows Credential Manager's 1280-character limit for UTF-16 values, #116).
   */
  refreshTokenLength = 0;

  constructor(readonly options: OAuthStubOptions = {}) {}

  issuer = "";

  /** Listen on `port` (a free one by default), on 127.0.0.1. */
  async start(port = 0): Promise<this> {
    if (this.options.jwt) {
      const { privateKey, publicKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
      const kid = randomBytes(8).toString("hex");
      this.signingKey = { privateKey, jwk: { ...publicKey.export({ format: "jwk" }), kid, use: "sig", alg: "RS256" } };
    }

    this.server = http.createServer((request, response) => void this.handle(request, response));
    await new Promise<void>((resolve) => this.server!.listen(port, "127.0.0.1", resolve));
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
        scopes_supported: ["openid", "profile", "offline_access", "Library", ...(this.options.jwt ? [this.options.jwt.audience] : [])],
        ...(this.signingKey ? { jwks_uri: `${this.issuer}/.well-known/jwks.json` } : {}),
      });
    }

    if (url.pathname === "/.well-known/jwks.json" && this.signingKey) {
      return json(response, 200, { keys: [this.signingKey.jwk] });
    }

    if (url.pathname === "/connect/authorize") {
      const query = Object.fromEntries(url.searchParams);
      this.authorizeRequests.push(query);
      if (query.code_challenge_method !== "S256" || !query.code_challenge || !query.redirect_uri?.startsWith("http://127.0.0.1:")) {
        return json(response, 400, { error: "invalid_request" });
      }

      const code = randomBytes(16).toString("hex");
      this.issued.push(code);
      this.codes.set(code, {
        challenge: query.code_challenge,
        redirectUri: query.redirect_uri,
        scope: query.scope ?? "",
        ...(this.browserUser(query.login_hint) ? { user: this.browserUser(query.login_hint) } : {}),
      });
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

        return this.issue(response, issued.scope, true, issued.user);
      }

      case "refresh_token": {
        const refreshed = this.refreshTokens.get(form.refresh_token ?? "");
        if (!refreshed || !this.refreshTokens.delete(form.refresh_token ?? "")) {
          return json(response, 400, { error: "invalid_grant", error_description: "The refresh token is no longer valid." });
        }

        return this.issue(response, refreshed.scope, true, refreshed.user);
      }

      case "password":
        if (!this.checkPassword(form.username, form.password)) {
          return json(response, 400, { error: "invalid_grant", error_description: "Invalid username or password." });
        }

        return this.issue(response, form.scope ?? "", true, this.options.users ? form.username : undefined);

      case "client_credentials":
        if (form.client_secret !== (this.options.clientSecret ?? "s3cret-client")) {
          return json(response, 401, { error: "invalid_client", error_description: "Invalid client secret." });
        }

        return this.issue(response, "", false);

      default:
        return json(response, 400, { error: "unsupported_grant_type" });
    }
  }

  /** The user a browser sign-in approves: the one `login_hint` names, else the first. */
  private browserUser(hint: string | undefined): string | undefined {
    const users = this.options.users;
    if (!users) {
      return undefined;
    }

    return hint && hint in users ? hint : Object.keys(users)[0];
  }

  private checkPassword(username: string | undefined, password: string | undefined): boolean {
    const users = this.options.users;
    if (users) {
      const user = username === undefined ? undefined : users[username];
      return user?.password !== undefined && user.password === password;
    }

    return username === (this.options.username ?? "alice") && password === (this.options.password ?? "correct horse");
  }

  private issue(response: http.ServerResponse, scope: string, allowRefresh = true, user?: string): void {
    const lifetime = this.options.accessTokenLifetimeSeconds ?? 3600;
    const accessToken = this.signingKey
      ? this.signJwt(scope, lifetime, user)
      : `at-${randomBytes(12).toString("hex")}`;
    this.issued.push(accessToken);
    this.accessTokens.set(accessToken, Date.now() + lifetime * 1000);
    const body: Record<string, unknown> = { access_token: accessToken, token_type: "Bearer", expires_in: lifetime };
    if (allowRefresh && !this.noRefreshTokens && scope.split(" ").includes("offline_access")) {
      const refreshToken = `rt-${randomBytes(12).toString("hex")}`.padEnd(this.refreshTokenLength, "x");
      this.issued.push(refreshToken);
      this.refreshTokens.set(refreshToken, { scope, ...(user ? { user } : {}) });
      body.refresh_token = refreshToken;
    }

    json(response, 200, body);
  }

  private signJwt(scope: string, lifetime: number, user: string | undefined): string {
    const key = this.signingKey!;
    const now = Math.floor(Date.now() / 1000);
    const sub = user ? this.options.users?.[user]?.sub : undefined;
    const header = encodeSegment({ alg: "RS256", typ: "at+jwt", kid: key.jwk.kid });
    const payload = encodeSegment({
      iss: this.issuer,
      aud: this.options.jwt!.audience,
      iat: now,
      nbf: now,
      exp: now + lifetime,
      jti: randomBytes(8).toString("hex"),
      scope,
      ...(sub ? { sub, preferred_username: user } : {}),
    });
    const signature = sign("sha256", Buffer.from(`${header}.${payload}`), key.privateKey).toString("base64url");
    return `${header}.${payload}.${signature}`;
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

/** One base64url-encoded JSON segment of a JWT. */
function encodeSegment(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
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
