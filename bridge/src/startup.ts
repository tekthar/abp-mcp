import { McpError } from "@modelcontextprotocol/sdk/types.js";
import type { Logger } from "./log.js";
import { isSignInError } from "./upstream/errors.js";
import type { Upstream } from "./upstream/upstream.js";

/** How long to wait between attempts while the host is unreachable. */
export const HOST_RETRY_MS = 500;

/**
 * Open the host session for Claude's `initialize`, trying again while the host is unreachable, until
 * `deadline` (epoch ms). True once the session is open. A sign-in problem or a JSON-RPC error from the host
 * ends the attempts at once: waiting would not fix either.
 */
export async function openHostSession(upstream: Upstream, deadline: number, log: Logger, now: () => number = Date.now): Promise<boolean> {
  for (;;) {
    try {
      await upstream.connect();
      return true;
    } catch (error) {
      if (isSignInError(error) || error instanceof McpError || now() + HOST_RETRY_MS >= deadline) {
        log.warn("Could not open the host session for initialize; answering with the bridge's own information", error);
        return false;
      }

      log.debug("The host is not reachable yet; trying again", error);
      await new Promise((resolve) => setTimeout(resolve, HOST_RETRY_MS).unref());
    }
  }
}

/** Resolves to `value` after `ms`, without keeping the process alive. */
export function after<T>(ms: number, value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), ms).unref());
}
