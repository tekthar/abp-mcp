import { SignInBreaker } from "../auth/breaker.js";
import { CredentialVault } from "../auth/vault.js";
import type { Io } from "../io.js";
import { Logger } from "../log.js";
import { profilesPath, stateDir } from "../paths.js";
import { AUTH_TYPES, loadProfiles, ProfileError, readProfilesDocument, STAGES, writeProfilesDocument } from "../profiles.js";
import { Redactor } from "../redact.js";

/** Print every profile. */
export function listProfiles(env: NodeJS.ProcessEnv, io: Io): void {
  const file = profilesPath(env);
  const profiles = loadProfiles(file);
  if (profiles.size === 0) {
    io.out(`No profiles in ${file}. Add one with \`abp-mcp-bridge profiles add <name>\`.`);
    return;
  }

  io.out(`Profiles in ${file}:`);
  for (const profile of profiles.values()) {
    io.out(`  ${profile.name}  ${profile.url}  ${profile.stage}, ${profile.auth.type} sign-in, ${profile.tools.preset} tools`);
  }
}

/** The options `profiles add` accepts on the command line; anything missing is asked for. */
export interface AddProfileOptions {
  readonly url?: string;
  readonly stage?: string;
  readonly auth?: string;
  readonly clientId?: string;
  readonly authority?: string;
  readonly scope?: string;
  readonly username?: string;
  readonly callbackPorts?: string;
  readonly preset?: string;
  readonly notes?: string;
  /** Ask for whatever is missing. Off when stdin is not a terminal. */
  readonly interactive: boolean;
}

/** Add (or replace) a profile, validating it before anything is written. */
export async function addProfile(name: string, options: AddProfileOptions, env: NodeJS.ProcessEnv, io: Io): Promise<void> {
  const ask = async (value: string | undefined, question: string, fallback?: string): Promise<string | undefined> => {
    if (value !== undefined || !options.interactive) {
      return value ?? fallback;
    }

    const answer = await io.prompt(fallback ? `${question} [${fallback}]: ` : `${question}: `);
    return answer === "" ? fallback : answer;
  };

  const url = await ask(options.url, "MCP endpoint URL (e.g. https://api.example.com/mcp)");
  if (!url) {
    throw new ProfileError("A profile needs --url.");
  }

  const stage = await ask(options.stage, `Stage (${STAGES.join(", ")})`, "development");
  const type = await ask(options.auth, `Sign-in (${AUTH_TYPES.join(", ")})`, "browser");
  const auth: Record<string, unknown> = { type };
  if (type === "browser" || type === "password" || type === "client_credentials") {
    auth.clientId = await ask(options.clientId, "OpenIddict client id");
    const authority = await ask(options.authority, "Authorization server URL (empty to discover it from the host)", "");
    if (authority) {
      auth.authority = authority;
    }

    const scope = await ask(options.scope, "Scopes (empty to use what the host advertises)", "");
    if (scope) {
      auth.scope = scope;
    }
  }

  if (type === "password" || (type === "browser" && options.username)) {
    auth.username = await ask(options.username, "User name");
  }

  if (type === "browser" && options.callbackPorts) {
    auth.callbackPorts = options.callbackPorts.split(",").map((port) => Number(port.trim()));
  }

  const spec: Record<string, unknown> = { url, stage, auth };
  if (options.preset) {
    spec.tools = { preset: options.preset };
  }

  if (options.notes) {
    spec.notes = options.notes;
  }

  const file = profilesPath(env);
  const document = readProfilesDocument(file);
  document.profiles[name] = spec;
  writeProfilesDocument(file, document);
  io.out(`Saved profile '${name}' to ${file}.`);
  if (stage === "production" && !options.preset) {
    io.out("Production profiles show read-only tools unless tools.preset is 'all'.");
  }

  if (type !== "none" && type !== "token") {
    io.out(`Next: abp-mcp-bridge login ${name}`);
  }
}

/** Remove a profile and everything stored for it. */
export async function removeProfile(name: string, env: NodeJS.ProcessEnv, io: Io): Promise<void> {
  const file = profilesPath(env);
  const document = readProfilesDocument(file);
  if (!(name in document.profiles)) {
    throw new ProfileError(`No profile named '${name}' in ${file}.`);
  }

  delete document.profiles[name];
  writeProfilesDocument(file, document);

  const state = stateDir(env);
  const vault = await CredentialVault.create(state, new Logger({ redactor: new Redactor() }), env);
  await vault.forget(name);
  SignInBreaker.forProfile(state, name).reset();
  io.out(`Removed profile '${name}' and its stored sign-in.`);
}
