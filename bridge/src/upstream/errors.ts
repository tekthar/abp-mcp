import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";
import { BreakerOpenError, NotSignedInError, OAuthError } from "../auth/errors.js";

/** The call reached the host (or might have), and its outcome is unknown. The bridge never re-sends it. */
export class MayHaveRunError extends Error {
  override readonly name = "MayHaveRunError";

  constructor(tool: string, host: string, cause: unknown) {
    super(
      `The connection to ${host} failed after the call to '${tool}' was sent, so it may have run. ` +
      "The bridge never re-sends a call: check the result in the application before trying again.", { cause });
  }
}

/** The request never left this machine, so nothing ran on the host. */
export class NotSentError extends Error {
  override readonly name = "NotSentError";

  constructor(host: string, cause: unknown) {
    super(`Could not reach ${host}; nothing was sent, so the call did not run.`, { cause });
  }
}

// Connection failures that happen before a request is written.
const NOT_SENT_CODES = new Set([
  "ECONNREFUSED", "ENOTFOUND", "EAI_AGAIN", "EHOSTUNREACH", "ENETUNREACH",
  "CERT_HAS_EXPIRED", "DEPTH_ZERO_SELF_SIGNED_CERT", "SELF_SIGNED_CERT_IN_CHAIN", "UNABLE_TO_VERIFY_LEAF_SIGNATURE",
  "ERR_TLS_CERT_ALTNAME_INVALID", "UND_ERR_CONNECT_TIMEOUT",
]);

/** The host's session for this client is gone (it answered 404). The request did not run. */
export function isSessionExpired(error: unknown): boolean {
  return error instanceof StreamableHTTPError && error.code === 404;
}

/** A sign-in problem the user must fix; nothing was sent. */
export function isSignInError(error: unknown): boolean {
  return error instanceof NotSignedInError || error instanceof BreakerOpenError || error instanceof OAuthError;
}

/** True when the request certainly never reached the host. */
export function wasNotSent(error: unknown): boolean {
  for (let current: unknown = error, depth = 0; current && depth < 5; depth++) {
    const code = (current as { code?: unknown }).code;
    if (typeof code === "string" && NOT_SENT_CODES.has(code)) {
      return true;
    }

    current = (current as { cause?: unknown }).cause;
  }

  return false;
}

/**
 * Turn a failed `tools/call` into the error Claude should see. A JSON-RPC error is the host's own answer and
 * passes through. Sign-in errors and refusals before dispatch (4xx) mean nothing ran. Anything else, such as a
 * timeout, a dropped connection or a 5xx, leaves the outcome unknown.
 */
export function classifyCallError(error: unknown, tool: string, host: string): Error {
  if (error instanceof McpError) {
    return error.code === ErrorCode.RequestTimeout || error.code === ErrorCode.ConnectionClosed
      ? new MayHaveRunError(tool, host, error)
      : error;
  }

  if (isSignInError(error)) {
    return error as Error;
  }

  if (error instanceof StreamableHTTPError && error.code !== undefined && error.code >= 400 && error.code < 500) {
    return error;
  }

  if (wasNotSent(error)) {
    return new NotSentError(host, error);
  }

  return new MayHaveRunError(tool, host, error);
}
