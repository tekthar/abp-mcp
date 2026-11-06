import type { Redactor } from "../redact.js";
import { OAuthError } from "./errors.js";
import { InsecureUrlError, normalizeUrl, requireSecureUrl, resourceCovers } from "./url-policy.js";

export type FetchLike = (url: string | URL, init?: RequestInit) => Promise<Response>;

/** A successful token response. */
export interface TokenSet {
  readonly accessToken: string;
  /** Epoch milliseconds. */
  readonly expiresAt: number;
  readonly refreshToken?: string;
}

/** The authorization server endpoints the bridge uses. */
export interface AuthorizationServer {
  readonly issuer: string;
  readonly tokenEndpoint: string;
  readonly authorizationEndpoint?: string;
  readonly revocationEndpoint?: string;
  readonly scopesSupported?: readonly string[];
}

/** Where and how to sign in for one MCP endpoint. */
export interface SignInTarget {
  readonly server: AuthorizationServer;
  /** The resource indicator to send (RFC 8707), from the host's protected-resource metadata. */
  readonly resource?: string;
  /** Scopes the host's protected-resource metadata asks for. */
  readonly resourceScopes?: readonly string[];
}

/** The authorization server's metadata names a different issuer than the one it was fetched for. */
export class IssuerMismatchError extends Error {
  override readonly name = "IssuerMismatchError";
}

const DEFAULT_LIFETIME_SECONDS = 3600;

/** POST a grant to the token endpoint. Every secret in the request and the response is registered for redaction. */
export async function requestToken(
  fetchImpl: FetchLike,
  tokenEndpoint: string,
  params: Record<string, string>,
  redactor: Redactor,
  now: () => number = Date.now,
): Promise<TokenSet> {
  for (const key of ["password", "client_secret"]) {
    redactor.addCredential(params[key]);
  }

  for (const key of ["code", "code_verifier", "refresh_token"]) {
    redactor.add(params[key]);
  }

  requireSecureUrl(tokenEndpoint, "The token endpoint");
  // A redirect would resend the credentials to wherever it points, so none is followed.
  const response = await fetchImpl(tokenEndpoint, {
    method: "POST",
    redirect: "error",
    headers: { "content-type": "application/x-www-form-urlencoded", accept: "application/json" },
    body: new URLSearchParams(params).toString(),
  });

  const body = await response.json().catch(() => undefined) as Record<string, unknown> | undefined;
  const accessToken = typeof body?.access_token === "string" ? body.access_token : undefined;
  if (!response.ok || !accessToken) {
    const error = typeof body?.error === "string" ? body.error : `http_${response.status}`;
    const description = typeof body?.error_description === "string" ? body.error_description : undefined;
    throw new OAuthError(error, description, response.status);
  }

  redactor.add(accessToken);
  const refreshToken = typeof body?.refresh_token === "string" ? body.refresh_token : undefined;
  redactor.add(refreshToken);
  redactor.add(typeof body?.id_token === "string" ? body.id_token : undefined);

  const lifetime = typeof body?.expires_in === "number" && body.expires_in > 0 ? body.expires_in : DEFAULT_LIFETIME_SECONDS;
  return { accessToken, expiresAt: now() + lifetime * 1000, ...(refreshToken ? { refreshToken } : {}) };
}

/** One challenge from a `WWW-Authenticate` header. */
export interface Challenge {
  readonly scheme: string;
  readonly params: Readonly<Record<string, string>>;
}

/** Parse a `WWW-Authenticate` header (RFC 9110 section 11.6.1) into its challenges. */
export function parseWwwAuthenticate(header: string): Challenge[] {
  const challenges: { scheme: string; params: Record<string, string> }[] = [];
  const token = /\s*([!#$%&'*+\-.^_`|~0-9A-Za-z]+)\s*/y;
  let index = 0;

  while (index < header.length) {
    token.lastIndex = index;
    const match = token.exec(header);
    if (!match) {
      index++;
      continue;
    }

    index = token.lastIndex;
    if (header[index] === "=") {
      // A parameter of the current challenge.
      index++;
      let value: string;
      if (header[index] === "\"") {
        let end = index + 1;
        value = "";
        while (end < header.length && header[end] !== "\"") {
          if (header[end] === "\\" && end + 1 < header.length) {
            end++;
          }

          value += header[end];
          end++;
        }

        index = end + 1;
      } else {
        const end = header.indexOf(",", index);
        value = (end === -1 ? header.slice(index) : header.slice(index, end)).trim();
        index = end === -1 ? header.length : end;
      }

      const current = challenges.at(-1);
      if (current) {
        current.params[match[1]!.toLowerCase()] = value;
      }
    } else {
      challenges.push({ scheme: match[1]!, params: {} });
    }

    while (index < header.length && (header[index] === "," || header[index] === " ")) {
      index++;
    }
  }

  return challenges;
}

/**
 * Find the authorization server for an MCP endpoint. Every URL taken from the host or the authorization
 * server must pass the same https rule as the profile's URLs (see `url-policy.ts`), the host's resource
 * metadata must describe `mcpUrl` (RFC 9728 section 3.3), and the server metadata's issuer must be the
 * issuer it was fetched for (RFC 8414 section 3.3).
 *
 * With `authority` set, only its metadata is read. Otherwise the endpoint's 401 challenge names its
 * protected-resource metadata (RFC 9728), which names the authorization server, whose metadata (OpenID Connect discovery, else RFC 8414) gives the endpoints.
 */
export async function discoverSignIn(fetchImpl: FetchLike, mcpUrl: string, authority?: string): Promise<SignInTarget> {
  if (authority) {
    return { server: await fetchAuthorizationServer(fetchImpl, authority) };
  }

  const metadataUrl = await findResourceMetadataUrl(fetchImpl, mcpUrl);
  if (!metadataUrl) {
    throw new Error(
      `${mcpUrl} does not advertise OAuth metadata (no resource_metadata in its 401 challenge, no RFC 9728 ` +
      "document). Set auth.authority (and auth.scope) in the profile.");
  }

  const metadata = await fetchJson(fetchImpl, metadataUrl);
  const resource = typeof metadata.resource === "string" ? metadata.resource : undefined;
  if (!resource) {
    throw new Error(`${metadataUrl} has no resource (RFC 9728 requires it). The bridge will not sign in with it.`);
  }

  if (!resourceCovers(resource, mcpUrl)) {
    throw new Error(
      `The resource metadata at ${metadataUrl} is for '${resource}', not for ${mcpUrl} (RFC 9728 section 3.3). ` +
      "The bridge will not sign in with it.");
  }

  const servers = Array.isArray(metadata.authorization_servers) ? metadata.authorization_servers : [];
  const issuer = typeof servers[0] === "string" ? servers[0] : undefined;
  if (!issuer) {
    throw new Error(`${metadataUrl} names no authorization server. Set auth.authority in the profile.`);
  }

  requireSecureUrl(issuer, `authorization_servers[0] in ${metadataUrl}`);
  return {
    server: await fetchAuthorizationServer(fetchImpl, issuer),
    resource,
    ...(isStringArray(metadata.scopes_supported) ? { resourceScopes: metadata.scopes_supported } : {}),
  };
}

/** The protected-resource metadata URL: from the 401 challenge, else the RFC 9728 well-known locations. */
export async function findResourceMetadataUrl(fetchImpl: FetchLike, mcpUrl: string): Promise<string | undefined> {
  const response = await fetchImpl(mcpUrl, { method: "GET", headers: { accept: "application/json, text/event-stream" } });
  await response.body?.cancel().catch(() => undefined);
  const header = response.headers.get("www-authenticate");
  if (header) {
    for (const challenge of parseWwwAuthenticate(header)) {
      const url = challenge.params.resource_metadata;
      if (url) {
        return requireSecureUrl(new URL(url, mcpUrl).toString(), `The resource_metadata URL in the 401 challenge from ${mcpUrl}`);
      }
    }
  }

  const endpoint = new URL(mcpUrl);
  const path = endpoint.pathname.replace(/\/+$/, "");
  for (const candidate of [`/.well-known/oauth-protected-resource${path}`, "/.well-known/oauth-protected-resource"]) {
    const url = new URL(candidate, endpoint.origin).toString();
    const probe = await fetchImpl(url, { headers: { accept: "application/json" } }).catch(() => undefined);
    if (probe?.ok) {
      await probe.body?.cancel().catch(() => undefined);
      return url;
    }

    await probe?.body?.cancel().catch(() => undefined);
  }

  return undefined;
}

/**
 * Read an authorization server's metadata: OpenID Connect discovery first, then RFC 8414. The metadata's
 * issuer must match `issuer`, and every endpoint must pass the https rule.
 */
export async function fetchAuthorizationServer(fetchImpl: FetchLike, issuer: string): Promise<AuthorizationServer> {
  requireSecureUrl(issuer, "The authorization server");
  const base = new URL(issuer);
  const path = base.pathname.replace(/\/+$/, "");
  const candidates = [
    new URL(`${path}/.well-known/openid-configuration`, base.origin).toString(),
    new URL(`/.well-known/oauth-authorization-server${path}`, base.origin).toString(),
  ];

  let lastError: unknown;
  for (const url of candidates) {
    try {
      const metadata = await fetchJson(fetchImpl, url);
      if (typeof metadata.token_endpoint !== "string") {
        throw new Error(`${url} has no token_endpoint.`);
      }

      if (typeof metadata.issuer !== "string" || !sameIssuer(metadata.issuer, issuer)) {
        throw new IssuerMismatchError(
          `${url} is for issuer '${String(metadata.issuer)}', not '${issuer}' (RFC 8414 section 3.3). ` +
          "The bridge will not use it.");
      }

      const endpoint = (field: string): string | undefined => {
        const value = metadata[field];
        return typeof value === "string" ? requireSecureUrl(value, `${field} in ${url}`) : undefined;
      };
      const tokenEndpoint = endpoint("token_endpoint")!;
      const authorizationEndpoint = endpoint("authorization_endpoint");
      const revocationEndpoint = endpoint("revocation_endpoint");

      return {
        issuer: metadata.issuer,
        tokenEndpoint,
        ...(authorizationEndpoint ? { authorizationEndpoint } : {}),
        ...(revocationEndpoint ? { revocationEndpoint } : {}),
        ...(isStringArray(metadata.scopes_supported) ? { scopesSupported: metadata.scopes_supported } : {}),
      };
    } catch (error) {
      if (error instanceof InsecureUrlError || error instanceof IssuerMismatchError) {
        throw error;
      }

      lastError = error;
    }
  }

  throw new Error(`Could not read the authorization server metadata for ${issuer}.`, { cause: lastError });
}

/**
 * The scopes to request: the profile's, else the host's resource scopes, plus `offline_access` when the
 * authorization server supports it so the bridge gets a refresh token.
 */
export function chooseScope(configured: string | undefined, target: SignInTarget): string | undefined {
  if (configured) {
    return configured;
  }

  const scopes = [...(target.resourceScopes ?? [])];
  if (scopes.length === 0) {
    return undefined;
  }

  if (target.server.scopesSupported?.includes("offline_access") && !scopes.includes("offline_access")) {
    scopes.push("offline_access");
  }

  return scopes.join(" ");
}

async function fetchJson(fetchImpl: FetchLike, url: string): Promise<Record<string, unknown>> {
  const response = await fetchImpl(url, { headers: { accept: "application/json" } });
  if (!response.ok) {
    await response.body?.cancel().catch(() => undefined);
    throw new Error(`GET ${url} returned ${response.status}.`);
  }

  const body = await response.json() as unknown;
  if (typeof body !== "object" || body === null) {
    throw new Error(`GET ${url} did not return a JSON object.`);
  }

  return body as Record<string, unknown>;
}

/** Issuers compare as identical URLs; a trailing slash alone does not make them differ. */
function sameIssuer(actual: string, expected: string): boolean {
  try {
    return normalizeUrl(actual) === normalizeUrl(expected);
  } catch {
    return false;
  }
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}
