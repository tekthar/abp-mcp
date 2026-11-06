/**
 * The one rule for every URL the bridge sends a credential to or hands to the OS: https, or plain http
 * only on a loopback host. It applies to the URLs written in a profile and, just as much, to every URL
 * discovered from the host or its authorization server, which the profile's author never saw.
 */

/** Thrown when a discovered URL breaks the policy. The message names the field and the URL. */
export class InsecureUrlError extends Error {
  override readonly name = "InsecureUrlError";
}

const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "[::1]"]);

/** True for `localhost`, `127.0.0.1` and `[::1]`. */
export function isLoopbackHost(hostname: string): boolean {
  return LOOPBACK_HOSTS.has(hostname.toLowerCase());
}

/** Why `value` is not allowed, or `undefined` when it is (https, or http on a loopback host). */
export function urlPolicyViolation(value: string): string | undefined {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return "is not a valid absolute URL";
  }

  if (url.protocol === "https:") {
    return undefined;
  }

  if (url.protocol === "http:") {
    return isLoopbackHost(url.hostname) ? undefined : "must use https (plain http is allowed only for localhost)";
  }

  return `must use https, not '${url.protocol}'`;
}

/** Return `value` when it passes the policy; otherwise throw an {@link InsecureUrlError} naming `where`. */
export function requireSecureUrl(value: string, where: string): string {
  const violation = urlPolicyViolation(value);
  if (violation) {
    throw new InsecureUrlError(`${where} ${violation}: '${value}'. The bridge refuses to use it.`);
  }

  return value;
}

/**
 * A URL in a comparable form: scheme and host lower-cased, default port dropped, no query or fragment,
 * and no trailing slash on the path.
 */
export function normalizeUrl(value: string): string {
  const url = new URL(value);
  return `${url.protocol}//${url.host}${url.pathname.replace(/\/+$/, "")}`;
}

/**
 * True when the protected-resource metadata's `resource` covers `requested` (RFC 9728 section 3.3):
 * same origin, and `requested` is that resource or a path under it.
 */
export function resourceCovers(resource: string, requested: string): boolean {
  let a: URL;
  let b: URL;
  try {
    a = new URL(normalizeUrl(resource));
    b = new URL(normalizeUrl(requested));
  } catch {
    return false;
  }

  if (a.origin !== b.origin) {
    return false;
  }

  return b.pathname === a.pathname || a.pathname === "" || a.pathname === "/" || b.pathname.startsWith(`${a.pathname}/`);
}
