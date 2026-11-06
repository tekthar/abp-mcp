import { decodeJwtClaims } from "../auth/token-manager.js";
import type { Io } from "../io.js";
import type { Runtime } from "../runtime.js";
import { Upstream } from "../upstream/upstream.js";

export interface LoginOptions {
  /** Save the password (password profiles) or client secret (client_credentials) in the OS keychain. */
  readonly save: boolean;
}

/** Sign in now, store the refresh token, and check the connection by listing tools. */
export async function login(runtime: Runtime, options: LoginOptions, io: Io): Promise<void> {
  const { profile, tokens, env } = runtime;
  const auth = profile.auth;

  switch (auth.type) {
    case "none":
      io.out(`Profile '${profile.name}' does not sign in.`);
      break;

    case "token":
      await tokens.getAccessToken({ interactive: false });
      io.out(`Profile '${profile.name}' uses the token from ${auth.tokenFile ?? auth.tokenEnv ?? "ABP_MCP_TOKEN"}.`);
      break;

    case "password": {
      io.err("Note: the password grant is deprecated in OAuth 2.1. Prefer browser sign-in where the server supports it.");
      const password = env.ABP_MCP_PASSWORD || await io.promptHidden(`Password for ${auth.username} at ${profile.name}: `);
      await tokens.signInWithPassword(password, options.save);
      if (options.save) {
        io.out("Saved the password in the OS keychain.");
      }

      break;
    }

    case "client_credentials": {
      const secret = env.ABP_MCP_CLIENT_SECRET || await io.promptHidden(`Client secret for ${auth.clientId} at ${profile.name}: `);
      await tokens.signInWithClientSecret(secret, options.save);
      if (options.save) {
        io.out("Saved the client secret in the OS keychain.");
      }

      break;
    }

    case "browser":
      io.out("Opening the browser to sign in...");
      await tokens.signInWithBrowser();
      break;
  }

  const token = await tokens.getAccessToken({ interactive: false });
  const claims = token ? decodeJwtClaims(token) : undefined;
  const who = claims?.preferred_username ?? claims?.unique_name ?? claims?.name ?? claims?.sub;
  io.out(`Signed in to '${profile.name}'${typeof who === "string" ? ` as ${who}` : ""}.`);

  if (auth.type === "browser" || auth.type === "password" || auth.type === "client_credentials") {
    const outcome = tokens.lastRefreshToken;
    if (outcome?.issued && outcome.error !== undefined) {
      // The server did issue one; storing it failed (#116). Say that, not that the server sent none.
      io.err(
        `The server issued a refresh token, but it could not be stored (${describe(outcome.error)}), so you will ` +
        "have to sign in again when this token expires.");
    } else if (outcome?.issued || await runtime.vault.getRefreshToken(profile.name)) {
      io.out(`The sign-in is kept in ${outcome?.issued && outcome.store ? outcome.store : await runtime.vault.refreshTokenStoreFor(profile.name)} and renewed automatically.`);
    } else if (auth.type !== "client_credentials" && !options.save) {
      io.err(
        "The server issued no refresh token, so you will have to sign in again when this token expires. " +
        "Add offline_access to auth.scope" + (auth.type === "password" ? ", or run login with --save-password." : "."));
    }
  }

  const upstream = new Upstream({ profile, tokens, log: runtime.log, version: runtime.version, interactive: false, onToolListChanged: () => undefined });
  try {
    const tools = await upstream.listTools();
    io.out(`The host lists ${tools.length} tool(s) for this user.`);
  } catch (error) {
    io.err(`Signed in, but listing tools failed: ${sentence((error as Error).message)} Run \`abp-mcp doctor ${profile.name}\`.`);
  } finally {
    await upstream.close();
  }
}

/** Forget the stored sign-in, saved password or secret, and any sign-in pause. */
export async function logout(runtime: Runtime, io: Io): Promise<void> {
  await runtime.tokens.logout();
  io.out(`Signed out of '${runtime.profile.name}'.`);
}

/** `text` as one sentence: ends with exactly one period, so it can be followed by another (#117). */
function sentence(text: string): string {
  const trimmed = text.trim();
  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
