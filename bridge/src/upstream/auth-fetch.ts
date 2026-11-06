import { AsyncLocalStorage } from "node:async_hooks";
import type { FetchLike } from "../auth/oauth.js";
import { HostRejectedTokenError, NotSignedInError } from "../auth/errors.js";
import type { TokenManager } from "../auth/token-manager.js";

/** The `tools/call` request in flight on the current async path, so its HTTP response can be watched. */
export interface CallScope {
  /** Set once the call has an outcome; later stream events are ignored. */
  settled: boolean;
  /** The response stream for the call broke, or ended without an answer. */
  onTransportFailure(error: unknown): void;
}

export const callScope = new AsyncLocalStorage<CallScope>();

export interface AuthenticatedFetchOptions {
  readonly tokens: TokenManager;
  /** Whether a request may open the browser to sign in. */
  readonly interactive: boolean;
  readonly baseFetch?: FetchLike;
}

/**
 * The fetch the MCP client uses. It adds the user's bearer token to every request; on a 401 it renews the
 * token and sends the request once more, which is safe because the host refused it before running anything.
 * For a `tools/call` it also watches the response stream, because the SDK reports a broken stream only to a
 * transport-wide error handler and leaves the call waiting.
 */
export function createAuthenticatedFetch(options: AuthenticatedFetchOptions): FetchLike {
  const base = options.baseFetch ?? globalThis.fetch;

  return async (url, init) => {
    const token = await options.tokens.getAccessToken({ interactive: options.interactive });
    let response = await base(url, withToken(init, token));

    if (response.status === 401 && token !== null) {
      options.tokens.invalidate(token);
      let renewed: string | null;
      try {
        renewed = await options.tokens.getAccessToken({ interactive: options.interactive });
      } catch (error) {
        // A token obtained moments ago and rejected at once is a scope or audience problem: say so, rather
        // than "not signed in" from the renewal that followed (#117).
        if (error instanceof NotSignedInError && recentlyObtained(options.tokens, token)) {
          throw hostRejected(options.tokens, response, error);
        }

        throw error;
      }

      if (renewed !== token) {
        await response.body?.cancel().catch(() => undefined);
        response = await base(url, withToken(init, renewed));
        if (response.status === 401 && renewed !== null && recentlyObtained(options.tokens, renewed)) {
          await response.body?.cancel().catch(() => undefined);
          throw hostRejected(options.tokens, response);
        }
      }
    }

    const scope = callScope.getStore();
    if (scope && response.ok && response.body && isToolCall(init)) {
      response = watchCallResponse(response, scope);
    }

    return response;
  };
}

function recentlyObtained(tokens: TokenManager, token: string): boolean {
  return typeof tokens.obtainedRecently === "function" && tokens.obtainedRecently(token);
}

/** The error for a 401 to a token just obtained, with the host's `WWW-Authenticate` error when it sent one. */
function hostRejected(tokens: TokenManager, response: Response, cause?: unknown): HostRejectedTokenError {
  const challenge = response.headers.get("www-authenticate");
  const error = challenge?.match(/error="?([^",]+)"?/)?.[1];
  const description = challenge?.match(/error_description="([^"]*)"/)?.[1];
  const detail = error ? `: ${error}${description ? ` (${description})` : ""}` : "";
  const name = tokens.profileName;
  return new HostRejectedTokenError(
    `The host rejected the access token just issued for profile '${name}' (HTTP 401${detail}). ` +
    "Check auth.scope and the token's audience: the token needs the API scope the host accepts. " +
    `\`abp-mcp doctor ${name}\` shows the scope the bridge requests.`,
    cause === undefined ? undefined : { cause });
}

function withToken(init: RequestInit | undefined, token: string | null): RequestInit {
  const headers = new Headers(init?.headers);
  if (token !== null) {
    headers.set("authorization", `Bearer ${token}`);
  }

  return { ...init, headers };
}

function isToolCall(init: RequestInit | undefined): boolean {
  if (init?.method !== "POST" || typeof init.body !== "string") {
    return false;
  }

  try {
    const message = JSON.parse(init.body) as unknown;
    const messages = Array.isArray(message) ? message : [message];
    return messages.some((item) => (item as { method?: unknown } | null)?.method === "tools/call");
  } catch {
    return false;
  }
}

/** Grace for the SDK to dispatch an answer that arrived just before the stream ended. */
const ANSWER_GRACE_MS = 100;

function watchCallResponse(response: Response, scope: CallScope): Response {
  const reader = response.body!.getReader();
  const body = new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const { done, value } = await reader.read();
        if (done) {
          controller.close();
          setTimeout(() => {
            if (!scope.settled) {
              scope.onTransportFailure(new Error("The host closed the response without answering the call."));
            }
          }, ANSWER_GRACE_MS).unref();
          return;
        }

        controller.enqueue(value);
      } catch (error) {
        scope.onTransportFailure(error);
        controller.error(error);
      }
    },
    cancel(reason) {
      return reader.cancel(reason);
    },
  });

  return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
}
