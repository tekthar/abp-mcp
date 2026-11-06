import fs from "node:fs";
import os from "node:os";
import type { Logger } from "../log.js";
import type { Profile } from "../profiles.js";
import { DEFAULT_CALLBACK_PORTS } from "../profiles.js";
import type { Redactor } from "../redact.js";
import type { SignInBreaker } from "./breaker.js";
import { browserSignIn, openInBrowser, type OpenUrl } from "./browser.js";
import { NotSignedInError, OAuthError, ScopeRequiredError } from "./errors.js";
import { chooseScope, discoverSignIn, requestToken, type FetchLike, type SignInTarget } from "./oauth.js";
import type { CredentialVault } from "./vault.js";

/** Tokens are renewed this long before they expire, so a request never leaves with one about to lapse. */
export const REFRESH_SKEW_MS = 60_000;

export interface TokenManagerOptions {
  readonly profile: Profile;
  readonly vault: CredentialVault;
  readonly breaker: SignInBreaker;
  readonly log: Logger;
  readonly redactor: Redactor;
  readonly fetch?: FetchLike;
  readonly openUrl?: OpenUrl;
  readonly env?: NodeJS.ProcessEnv;
  readonly now?: () => number;
  /** How long the browser flow waits for the user. */
  readonly browserTimeoutMs?: number;
}

/**
 * Supplies the access token for every request to the host. In order it uses: the cached token, a refresh
 * (rolling refresh tokens are stored again on every use), a fresh grant from a saved or environment
 * credential, and, when allowed, the browser. Concurrent callers share one attempt.
 */
export class TokenManager {
  private current?: { readonly token: string; readonly expiresAt: number };
  /** The attempt running now; `interactive` says whether it may open the browser. */
  private inflight?: { readonly interactive: boolean; readonly promise: Promise<string> };
  private target?: Promise<SignInTarget>;
  /** The last token a grant returned, and when, so a 401 for it can be told apart from an expired sign-in. */
  private lastIssued?: { readonly token: string; readonly at: number };
  private warnedFileFallback = false;
  private readonly fetch: FetchLike;

  /**
   * What happened to the refresh token of the last grant: whether the server issued one, and where it was
   * stored or why it could not be. `login` reports this instead of guessing from the vault (#116).
   */
  lastRefreshToken?: { readonly issued: false } | { readonly issued: true; readonly store?: string; readonly error?: unknown };
  private readonly env: NodeJS.ProcessEnv;
  private readonly now: () => number;

  constructor(private readonly options: TokenManagerOptions) {
    this.fetch = options.fetch ?? globalThis.fetch;
    this.env = options.env ?? process.env;
    this.now = options.now ?? Date.now;
  }

  private get profile(): Profile {
    return this.options.profile;
  }

  /**
   * True when `token` is the one this process obtained in the last `withinMs` (default two minutes). A 401
   * for such a token means the host rejects what the authorization server issues, not that it expired (#117).
   */
  obtainedRecently(token: string, withinMs = 120_000): boolean {
    return this.lastIssued?.token === token && this.now() - this.lastIssued.at < withinMs;
  }

  /** The profile's name, for messages. */
  get profileName(): string {
    return this.profile.name;
  }

  /** False for `none` profiles, whose requests carry no token. */
  get requiresSignIn(): boolean {
    return this.profile.auth.type !== "none";
  }

  /**
   * The current access token, or `null` for a `none` profile. With `interactive`, a browser profile opens the
   * browser when nothing else works; without it, that case throws {@link NotSignedInError}.
   */
  async getAccessToken(options: { interactive: boolean }): Promise<string | null> {
    if (!this.requiresSignIn) {
      return null;
    }

    if (this.current && this.current.expiresAt - REFRESH_SKEW_MS > this.now()) {
      return this.current.token;
    }

    const running = this.inflight;
    if (running && !running.interactive && options.interactive && this.profile.auth.type === "browser") {
      // A non-interactive attempt fails with NotSignedInError where this caller would open the browser: wait for
      // it (it may still renew silently), then start, or join, an interactive attempt.
      return running.promise.catch((error: unknown) => {
        if (error instanceof NotSignedInError) {
          return this.getAccessToken({ interactive: true });
        }

        throw error;
      });
    }

    if (running) {
      return running.promise;
    }

    const attempt = {
      interactive: options.interactive,
      promise: this.acquire(options.interactive).finally(() => {
        if (this.inflight === attempt) {
          this.inflight = undefined;
        }
      }),
    };
    this.inflight = attempt;
    return attempt.promise;
  }

  /** True when a token is cached and not about to expire. Never waits, never signs in. */
  hasFreshToken(): boolean {
    return !this.requiresSignIn || (this.current !== undefined && this.current.expiresAt - REFRESH_SKEW_MS > this.now());
  }

  /** Drop `token` after the host rejected it, so the next request renews it. */
  invalidate(token: string): void {
    if (this.current?.token === token) {
      this.current = undefined;
    }
  }

  /** True when a token can be had without the user: cached, refreshable, or from a saved credential. */
  async canSignInSilently(): Promise<boolean> {
    const auth = this.profile.auth;
    if (auth.type === "none") {
      return true;
    }

    if (auth.type === "token") {
      return this.readStaticToken() !== undefined;
    }

    if (this.options.breaker.status().open) {
      return false;
    }

    if (this.current || await this.options.vault.getRefreshToken(this.profile.name)) {
      return true;
    }

    if (auth.type === "password") {
      return (await this.password()) !== undefined;
    }

    return auth.type === "client_credentials" && (await this.clientSecret()) !== undefined;
  }

  /** Sign in with a password the user just typed (`login`), optionally saving it to the OS keychain. */
  async signInWithPassword(password: string, save: boolean): Promise<void> {
    this.options.redactor.addCredential(password);
    this.current = undefined;
    await this.passwordGrant(password);
    if (save) {
      await this.options.vault.setSecret(this.profile.name, "password", password);
    }
  }

  /** Sign in with a client secret the user just typed (`login`), optionally saving it to the OS keychain. */
  async signInWithClientSecret(secret: string, save: boolean): Promise<void> {
    this.options.redactor.addCredential(secret);
    this.current = undefined;
    await this.clientCredentialsGrant(secret);
    if (save) {
      await this.options.vault.setSecret(this.profile.name, "client_secret", secret);
    }
  }

  /** Run the browser flow now (`login`), whatever is stored. */
  async signInWithBrowser(): Promise<void> {
    this.current = undefined;
    await this.browserGrant();
  }

  /** Forget every credential for the profile, revoking the refresh token first when the server allows it. */
  async logout(): Promise<void> {
    const refreshToken = await this.options.vault.getRefreshToken(this.profile.name);
    if (refreshToken && this.profile.auth.type !== "token") {
      try {
        const target = await this.signInTarget();
        if (target.server.revocationEndpoint) {
          this.options.redactor.add(refreshToken);
          await this.fetch(target.server.revocationEndpoint, {
            method: "POST",
            redirect: "error",
            headers: { "content-type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams({ token: refreshToken, token_type_hint: "refresh_token", client_id: this.profile.auth.clientId ?? "" }).toString(),
          });
        }
      } catch (error) {
        this.options.log.debug("Could not revoke the refresh token", error);
      }
    }

    this.current = undefined;
    await this.options.vault.forget(this.profile.name);
    this.options.breaker.reset();
  }

  /** The authorization server and resource to sign in against. Discovered once per process. */
  signInTarget(): Promise<SignInTarget> {
    this.target ??= discoverSignIn(this.fetch, this.profile.url, this.profile.auth.authority).then((target) => {
      for (const note of target.notes ?? []) {
        this.options.log.warn(`Profile '${this.profile.name}': ${note}.`);
      }

      return target;
    }, (error: unknown) => {
      this.target = undefined;
      throw error;
    });
    return this.target;
  }

  private async acquire(interactive: boolean): Promise<string> {
    const auth = this.profile.auth;
    if (auth.type === "token") {
      return this.staticToken();
    }

    this.options.breaker.check();
    const refreshed = await this.tryRefresh();
    if (refreshed) {
      return refreshed;
    }

    const name = this.profile.name;
    if (auth.type === "password") {
      const password = await this.password();
      if (!password) {
        throw new NotSignedInError(
          `Profile '${name}' is not signed in. Run \`abp-mcp login ${name}\`, or set ABP_MCP_PASSWORD.`);
      }

      return this.passwordGrant(password);
    }

    if (auth.type === "client_credentials") {
      const secret = await this.clientSecret();
      if (!secret) {
        throw new NotSignedInError(
          `Profile '${name}' has no client secret. Run \`abp-mcp login ${name} --save-secret\`, or set ABP_MCP_CLIENT_SECRET.`);
      }

      return this.clientCredentialsGrant(secret);
    }

    if (!interactive) {
      throw new NotSignedInError(`Profile '${name}' is not signed in. Run \`abp-mcp login ${name}\`.`);
    }

    return this.browserGrant();
  }

  /**
   * Renew from the stored refresh token. Another bridge on the same profile (Claude Code and Claude Desktop)
   * may have used and rotated it since this one read it: on a rejection, the vault is read again and a newer
   * token is tried once. Only the token that was rejected is deleted, never one another process stored since.
   */
  private async tryRefresh(): Promise<string | undefined> {
    const name = this.profile.name;
    let refreshToken = await this.options.vault.getRefreshToken(name);
    for (let attempt = 0; refreshToken && attempt < 2; attempt++) {
      this.options.redactor.add(refreshToken);
      try {
        return await this.grant({ grant_type: "refresh_token", refresh_token: refreshToken }, { credentialGrant: false });
      } catch (error) {
        if (!(error instanceof OAuthError && error.isCredentialRejection)) {
          throw error;
        }
      }

      const latest = await this.options.vault.getRefreshToken(name);
      if (attempt === 0 && latest && latest !== refreshToken) {
        this.options.log.debug(`Another process renewed the sign-in for profile '${name}'; using its refresh token.`);
        refreshToken = latest;
        continue;
      }

      // Expired or revoked; fall through to the profile's other ways of signing in.
      this.options.log.info(`The stored sign-in for profile '${name}' has expired or was revoked.`);
      await this.options.vault.deleteRefreshTokenIf(name, refreshToken);
      return undefined;
    }

    return undefined;
  }

  private passwordGrant(password: string): Promise<string> {
    return this.grant(
      { grant_type: "password", username: this.profile.auth.username ?? "", password },
      { credentialGrant: true });
  }

  private clientCredentialsGrant(secret: string): Promise<string> {
    return this.grant({ grant_type: "client_credentials", client_secret: secret }, { credentialGrant: true });
  }

  private async browserGrant(): Promise<string> {
    const target = await this.signInTarget();
    if (!target.server.authorizationEndpoint) {
      throw new Error(`The authorization server ${target.server.issuer} has no authorization endpoint for browser sign-in.`);
    }

    const auth = this.profile.auth;
    const scope = this.requireScope(target);
    const result = await browserSignIn({
      authorizationEndpoint: target.server.authorizationEndpoint,
      clientId: auth.clientId ?? "",
      scope,
      ...(target.resource ? { resource: target.resource } : {}),
      ...(auth.username ? { loginHint: auth.username } : {}),
      ports: auth.callbackPorts ?? DEFAULT_CALLBACK_PORTS,
      redirectHost: auth.redirectHost ?? "127.0.0.1",
      profileName: this.profile.name,
      openUrl: this.options.openUrl ?? openInBrowser,
      log: this.options.log,
      ...(this.options.browserTimeoutMs ? { timeoutMs: this.options.browserTimeoutMs } : {}),
    });

    return this.grant(
      { grant_type: "authorization_code", code: result.code, redirect_uri: result.redirectUri, code_verifier: result.verifier },
      { credentialGrant: false, omitScope: true });
  }

  /**
   * The scope to request. Without one, the authorization server issues a token with no API scope (so no
   * audience the host accepts) and no refresh token, and the host's 401 looks like a missing sign-in (#117).
   */
  private requireScope(target: SignInTarget): string {
    const scope = chooseScope(this.profile.auth.scope, target);
    if (!scope) {
      throw new ScopeRequiredError(
        `No scope to request for profile '${this.profile.name}': the profile sets no auth.scope and the host ` +
        "advertises none. Set auth.scope to the API scope the host expects, with offline_access for a refresh token.");
    }

    return scope;
  }

  private async grant(
    params: Record<string, string>,
    options: { credentialGrant: boolean; omitScope?: boolean },
  ): Promise<string> {
    const target = await this.signInTarget();
    const scope = options.omitScope || params.grant_type === "refresh_token" ? undefined : this.requireScope(target);
    const request: Record<string, string> = {
      ...params,
      client_id: this.profile.auth.clientId ?? "",
      ...(scope && params.grant_type !== "refresh_token" ? { scope } : {}),
      ...(target.resource ? { resource: target.resource } : {}),
    };

    let tokens;
    try {
      tokens = await requestToken(this.fetch, target.server.tokenEndpoint, request, this.options.redactor, this.now);
    } catch (error) {
      if (options.credentialGrant && error instanceof OAuthError && error.isCredentialRejection) {
        this.options.breaker.trip(error.error);
      }

      throw error;
    }

    this.current = { token: tokens.accessToken, expiresAt: tokens.expiresAt };
    this.lastIssued = { token: tokens.accessToken, at: this.now() };
    if (options.credentialGrant || params.grant_type === "authorization_code") {
      this.options.breaker.reset();
    }

    if (tokens.refreshToken) {
      try {
        const stored = await this.options.vault.setRefreshToken(this.profile.name, tokens.refreshToken);
        this.lastRefreshToken = { issued: true, store: stored.store };
        if (stored.keychainRefused !== undefined && !this.warnedFileFallback) {
          this.warnedFileFallback = true;
          this.options.log.info(
            `The OS keychain refused the refresh token for profile '${this.profile.name}' ` +
            `(${describe(stored.keychainRefused)}); it is kept in ${stored.store} instead.`);
        }
      } catch (error) {
        this.lastRefreshToken = { issued: true, error };
        this.options.log.warn(`Could not store the refresh token for profile '${this.profile.name}'`, error);
      }
    } else if (params.grant_type !== "refresh_token") {
      this.lastRefreshToken = { issued: false };
    }

    return tokens.accessToken;
  }

  private staticToken(): string {
    const token = this.readStaticToken();
    if (!token) {
      const auth = this.profile.auth;
      throw new NotSignedInError(auth.tokenFile
        ? `Profile '${this.profile.name}' reads its token from ${auth.tokenFile}, which is missing or empty.`
        : `Profile '${this.profile.name}' reads its token from ${auth.tokenEnv ?? "ABP_MCP_TOKEN"}, which is not set.`);
    }

    this.options.redactor.add(token);
    this.current = { token, expiresAt: jwtExpiry(token) ?? Number.POSITIVE_INFINITY };
    return token;
  }

  private readStaticToken(): string | undefined {
    const auth = this.profile.auth;
    if (auth.tokenFile) {
      try {
        const file = auth.tokenFile.replace(/^~(?=$|[\\/])/, os.homedir());
        return fs.readFileSync(file, "utf8").trim() || undefined;
      } catch {
        return undefined;
      }
    }

    return this.env[auth.tokenEnv ?? "ABP_MCP_TOKEN"]?.trim() || undefined;
  }

  private async password(): Promise<string | undefined> {
    const password = this.env.ABP_MCP_PASSWORD || await this.options.vault.getSecret(this.profile.name, "password") || undefined;
    this.options.redactor.addCredential(password);
    return password;
  }

  private async clientSecret(): Promise<string | undefined> {
    const secret = this.env.ABP_MCP_CLIENT_SECRET || await this.options.vault.getSecret(this.profile.name, "client_secret") || undefined;
    this.options.redactor.addCredential(secret);
    return secret;
  }
}

/** A JWT's claims, unverified: for display and expiry only, never for trust decisions. */
export function decodeJwtClaims(token: string): Record<string, unknown> | undefined {
  const payload = token.split(".")[1];
  if (!payload) {
    return undefined;
  }

  try {
    const claims = JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as unknown;
    return typeof claims === "object" && claims !== null ? claims as Record<string, unknown> : undefined;
  } catch {
    return undefined;
  }
}

function jwtExpiry(token: string): number | undefined {
  const exp = decodeJwtClaims(token)?.exp;
  return typeof exp === "number" ? exp * 1000 : undefined;
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
