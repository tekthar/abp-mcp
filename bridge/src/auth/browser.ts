import { spawn } from "node:child_process";
import http from "node:http";
import type { AddressInfo } from "node:net";
import type { Logger } from "../log.js";
import { NotSignedInError, OAuthError } from "./errors.js";
import { createPkce } from "./pkce.js";

export type OpenUrl = (url: string) => void | Promise<void>;

export interface BrowserSignInOptions {
  readonly authorizationEndpoint: string;
  readonly clientId: string;
  readonly scope?: string;
  readonly resource?: string;
  readonly loginHint?: string;
  /** Loopback ports tried in order; the first free one is used. */
  readonly ports: readonly number[];
  readonly redirectHost: "127.0.0.1" | "localhost";
  readonly profileName: string;
  readonly openUrl: OpenUrl;
  readonly log: Logger;
  readonly timeoutMs?: number;
}

/** What the token exchange needs after the user approved the sign-in. */
export interface AuthorizationCode {
  readonly code: string;
  readonly redirectUri: string;
  readonly verifier: string;
}

const DEFAULT_TIMEOUT_MS = 5 * 60 * 1000;

/**
 * The authorization code flow with PKCE (S256) through the system browser and a loopback redirect
 * (RFC 8252). Resolves with the code once the browser comes back with the matching `state`.
 */
export async function browserSignIn(options: BrowserSignInOptions): Promise<AuthorizationCode> {
  const pkce = createPkce();
  const { server, port } = await listenOnFirstFreePort(options.ports, options.redirectHost);
  const redirectUri = `http://${options.redirectHost}:${port}/callback`;

  const authorize = new URL(options.authorizationEndpoint);
  authorize.searchParams.set("response_type", "code");
  authorize.searchParams.set("client_id", options.clientId);
  authorize.searchParams.set("redirect_uri", redirectUri);
  authorize.searchParams.set("state", pkce.state);
  authorize.searchParams.set("code_challenge", pkce.challenge);
  authorize.searchParams.set("code_challenge_method", "S256");
  if (options.scope) {
    authorize.searchParams.set("scope", options.scope);
  }

  if (options.resource) {
    authorize.searchParams.set("resource", options.resource);
  }

  if (options.loginHint) {
    authorize.searchParams.set("login_hint", options.loginHint);
  }

  const code = new Promise<string>((resolve, reject) => {
    const timer = setTimeout(() => reject(new NotSignedInError(
      `Browser sign-in for profile '${options.profileName}' timed out. Run \`abp-mcp login ${options.profileName}\` to try again.`)),
      options.timeoutMs ?? DEFAULT_TIMEOUT_MS);
    timer.unref();

    server.on("request", (request, response) => {
      const url = new URL(request.url ?? "/", redirectUri);
      if (url.pathname !== "/callback") {
        response.writeHead(404).end();
        return;
      }

      clearTimeout(timer);
      const error = url.searchParams.get("error");
      const returnedState = url.searchParams.get("state");
      const returnedCode = url.searchParams.get("code");
      if (error) {
        finish(response, 400, `Sign-in failed: ${error}. You can close this tab.`);
        reject(new OAuthError(error, url.searchParams.get("error_description") ?? undefined));
      } else if (returnedState !== pkce.state) {
        finish(response, 400, "Sign-in failed: the response did not match this sign-in request. You can close this tab.");
        reject(new OAuthError("invalid_state", "The authorization response's state did not match the request."));
      } else if (!returnedCode) {
        finish(response, 400, "Sign-in failed: no authorization code. You can close this tab.");
        reject(new OAuthError("invalid_request", "The authorization response had no code."));
      } else {
        finish(response, 200, `Signed in to ${options.profileName}. You can close this tab and return to Claude.`);
        resolve(returnedCode);
      }
    });
  });

  // The callback can settle `code` while the browser is still being opened; it is awaited below.
  code.catch(() => undefined);

  try {
    options.log.info(`Opening the browser to sign in to profile '${options.profileName}'. If it does not open, visit: ${authorize}`);
    await options.openUrl(authorize.toString());
    return { code: await code, redirectUri, verifier: pkce.verifier };
  } finally {
    server.close();
    server.closeAllConnections();
  }
}

/** Open a URL in the user's default browser. */
export const openInBrowser: OpenUrl = (url) => {
  // rundll32 avoids cmd.exe, which would interpret the '&' in the query string.
  const [command, args] = process.platform === "win32"
    ? ["rundll32", ["url.dll,FileProtocolHandler", url]]
    : process.platform === "darwin" ? ["open", [url]] : ["xdg-open", [url]];

  const child = spawn(command as string, args as string[], { detached: true, stdio: "ignore" });
  child.on("error", () => undefined);
  child.unref();
};

/**
 * Listen for the callback on the first free port. With `localhost` in the redirect URI, the browser may resolve
 * it to `::1` and not fall back to IPv4, so the same port is also opened on `::1` and its requests handed to the
 * returned server. A machine without IPv6 just skips that; a port taken on `::1` moves on to the next port.
 */
export async function listenOnFirstFreePort(ports: readonly number[], redirectHost: "127.0.0.1" | "localhost" = "127.0.0.1"): Promise<{ server: http.Server; port: number }> {
  let lastError: unknown;
  for (const port of ports) {
    const server = http.createServer();
    try {
      await listen(server, port, "127.0.0.1");
      const bound = (server.address() as AddressInfo).port;
      if (redirectHost === "localhost") {
        await alsoListenOnIpv6(server, bound);
      }

      return { server, port: bound };
    } catch (error) {
      lastError = error;
      server.close();
    }
  }

  throw new NotSignedInError(
    `None of the browser callback ports (${ports.join(", ")}) is free. Close what uses them or add another port ` +
    "to auth.callbackPorts (and register its redirect URI with the client).", { cause: lastError });
}

function listen(server: http.Server, port: number, host: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => {
      server.off("error", reject);
      resolve();
    });
  });
}

const NO_IPV6 = new Set(["EADDRNOTAVAIL", "EAFNOSUPPORT"]);

async function alsoListenOnIpv6(primary: http.Server, port: number): Promise<void> {
  const ipv6 = http.createServer((request, response) => primary.emit("request", request, response));
  try {
    await listen(ipv6, port, "::1");
  } catch (error) {
    ipv6.close();
    if (NO_IPV6.has((error as { code?: unknown }).code as string)) {
      return;
    }

    throw error;
  }

  // browserSignIn closes the primary server when it is done; take the IPv6 listener down with it.
  const close = primary.close.bind(primary);
  primary.close = ((callback?: (error?: Error) => void) => {
    ipv6.close();
    ipv6.closeAllConnections();
    return close(callback);
  }) as typeof primary.close;
}

function finish(response: http.ServerResponse, status: number, message: string): void {
  const escaped = message.replace(/[&<>"']/g, (char) => `&#${char.charCodeAt(0)};`);
  response.writeHead(status, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
  response.end(`<!doctype html><meta charset="utf-8"><title>abp-mcp</title><p style="font:16px system-ui;margin:2em">${escaped}</p>`);
}
