import fs from "node:fs";
import { SignInBreaker } from "./auth/breaker.js";
import type { OpenUrl } from "./auth/browser.js";
import type { FetchLike } from "./auth/oauth.js";
import { TokenManager } from "./auth/token-manager.js";
import { CredentialVault } from "./auth/vault.js";
import { Logger } from "./log.js";
import { stateDir } from "./paths.js";
import { resolveProfile, type Profile } from "./profiles.js";
import { Redactor } from "./redact.js";

/** The pieces every command needs for one profile. */
export interface Runtime {
  readonly profile: Profile;
  readonly env: NodeJS.ProcessEnv;
  readonly version: string;
  readonly redactor: Redactor;
  readonly log: Logger;
  readonly vault: CredentialVault;
  readonly breaker: SignInBreaker;
  readonly tokens: TokenManager;
  readonly stateDir: string;
}

export interface RuntimeOptions {
  readonly env?: NodeJS.ProcessEnv;
  readonly fetch?: FetchLike;
  readonly openUrl?: OpenUrl;
  readonly logSink?: (line: string) => void;
}

export async function createRuntime(profileName: string, options: RuntimeOptions = {}): Promise<Runtime> {
  const env = options.env ?? process.env;
  const redactor = new Redactor();
  const log = options.logSink
    ? new Logger({ redactor, debug: env.ABP_MCP_DEBUG === "1", sink: options.logSink, ...(env.ABP_MCP_LOG_FILE ? { file: env.ABP_MCP_LOG_FILE } : {}) })
    : Logger.fromEnv(redactor, env);
  const profile = resolveProfile(profileName, env);
  const state = stateDir(env);
  const vault = await CredentialVault.create(state, log, env);
  const breaker = SignInBreaker.forProfile(state, profile.name);
  const tokens = new TokenManager({
    profile, vault, breaker, log, redactor, env,
    ...(options.fetch ? { fetch: options.fetch } : {}),
    ...(options.openUrl ? { openUrl: options.openUrl } : {}),
  });

  return { profile, env, version: packageVersion(), redactor, log, vault, breaker, tokens, stateDir: state };
}

/** This package's version. */
export function packageVersion(): string {
  try {
    const manifest = JSON.parse(fs.readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version?: string };
    return manifest.version ?? "0.0.0";
  } catch {
    return "0.0.0";
  }
}
