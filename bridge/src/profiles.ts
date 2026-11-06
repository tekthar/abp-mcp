import fs from "node:fs";
import path from "node:path";
import { urlPolicyViolation } from "./auth/url-policy.js";
import { profilesPath } from "./paths.js";

export type Stage = "development" | "staging" | "production";
export const STAGES: readonly Stage[] = ["development", "staging", "production"];

export type AuthType = "none" | "token" | "browser" | "password" | "client_credentials";
export const AUTH_TYPES: readonly AuthType[] = ["none", "token", "browser", "password", "client_credentials"];

/** How the bridge signs in. Secrets never live here: they come from the OS keychain, an env var or a prompt. */
export interface AuthConfig {
  readonly type: AuthType;
  /** OpenIddict client id (browser, password, client_credentials). */
  readonly clientId?: string;
  /** The authorization server. Discovered from the host (RFC 9728) when omitted. */
  readonly authority?: string;
  /** Requested scopes, space-separated. Taken from the host's resource metadata when omitted. */
  readonly scope?: string;
  /** Sign-in name for the password grant, or a login hint for the browser. */
  readonly username?: string;
  /** Loopback ports for the browser callback, tried in order. Each must be a registered redirect URI. */
  readonly callbackPorts?: readonly number[];
  /** Host name in the browser redirect URI. */
  readonly redirectHost?: "127.0.0.1" | "localhost";
  /** `token`: the env var holding the bearer token (default `ABP_MCP_TOKEN`). */
  readonly tokenEnv?: string;
  /** `token`: a file holding the bearer token. */
  readonly tokenFile?: string;
}

export type ToolPreset = "all" | "read-only";

export interface ToolsConfig {
  readonly preset: ToolPreset;
  /** When set, only tools matching one of these globs are shown. */
  readonly allow: readonly string[];
  /** Tools matching these globs are hidden, whatever else matches. */
  readonly deny: readonly string[];
  /** Tools to show even though the preset hides them, e.g. a POST endpoint that only reads. */
  readonly include: readonly string[];
}

/** One environment the bridge can connect to. */
export interface Profile {
  readonly name: string;
  /** The AbpMcp endpoint, e.g. `https://api.example.com/mcp`. */
  readonly url: string;
  readonly stage: Stage;
  readonly auth: AuthConfig;
  readonly tools: ToolsConfig;
  /** Guidance appended to the server instructions Claude sees. */
  readonly notes?: string;
  /** How long a tool call may run before the bridge gives up on it. */
  readonly timeoutSeconds: number;
}

export const DEFAULT_CALLBACK_PORTS: readonly number[] = [33418, 33419, 33420];
export const DEFAULT_TIMEOUT_SECONDS = 300;
const NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/** A profile file or profile that does not validate. The message names the offending key. */
export class ProfileError extends Error {}

const PROFILE_KEYS = new Set(["url", "stage", "auth", "tools", "notes", "timeoutSeconds"]);
const AUTH_KEYS = new Set(["type", "clientId", "authority", "scope", "username", "callbackPort", "callbackPorts", "redirectHost", "tokenEnv", "tokenFile"]);
const TOOLS_KEYS = new Set(["preset", "allow", "deny", "include"]);

/** Validate a parsed profiles document and return its profiles, defaults applied. */
export function parseProfiles(document: unknown): Map<string, Profile> {
  const root = asObject(document, "the profiles file");
  for (const key of Object.keys(root)) {
    if (key !== "profiles" && key !== "$schema") {
      throw new ProfileError(`Unknown key '${key}' in the profiles file. Expected 'profiles'.`);
    }
  }

  const profiles = new Map<string, Profile>();
  const entries = asObject(root.profiles ?? {}, "profiles");
  for (const [name, value] of Object.entries(entries)) {
    profiles.set(name, parseProfile(name, value));
  }

  return profiles;
}

/** Validate one profile. */
export function parseProfile(name: string, value: unknown): Profile {
  const where = `profiles.${name}`;
  if (!NAME.test(name)) {
    throw new ProfileError(`${where}: a profile name uses letters, digits, '.', '_' and '-' (at most 64).`);
  }

  const spec = asObject(value, where);
  rejectUnknown(spec, PROFILE_KEYS, where);

  const url = parseUrl(requireString(spec.url, `${where}.url`), `${where}.url`);
  const stage = optionalEnum(spec.stage, STAGES, `${where}.stage`) ?? "development";
  const auth = parseAuth(spec.auth, `${where}.auth`);
  const toolsSpec = spec.tools === undefined ? {} : asObject(spec.tools, `${where}.tools`);
  rejectUnknown(toolsSpec, TOOLS_KEYS, `${where}.tools`);

  // Production is read-only unless the profile asks for more.
  const preset = optionalEnum(toolsSpec.preset, ["all", "read-only"] as const, `${where}.tools.preset`)
    ?? (stage === "production" ? "read-only" : "all");

  const timeoutSeconds = spec.timeoutSeconds === undefined
    ? DEFAULT_TIMEOUT_SECONDS
    : requirePositiveNumber(spec.timeoutSeconds, `${where}.timeoutSeconds`);

  return {
    name,
    url,
    stage,
    auth,
    tools: {
      preset,
      allow: optionalStringArray(toolsSpec.allow, `${where}.tools.allow`),
      deny: optionalStringArray(toolsSpec.deny, `${where}.tools.deny`),
      include: optionalStringArray(toolsSpec.include, `${where}.tools.include`),
    },
    ...(spec.notes === undefined ? {} : { notes: requireString(spec.notes, `${where}.notes`) }),
    timeoutSeconds,
  };
}

function parseAuth(value: unknown, where: string): AuthConfig {
  const spec = asObject(value ?? { type: "none" }, where);
  rejectUnknown(spec, AUTH_KEYS, where);
  const type = optionalEnum(spec.type, AUTH_TYPES, `${where}.type`);
  if (!type) {
    throw new ProfileError(`${where}.type is required: one of ${AUTH_TYPES.join(", ")}.`);
  }

  const clientId = spec.clientId === undefined ? undefined : requireString(spec.clientId, `${where}.clientId`);
  if ((type === "browser" || type === "password" || type === "client_credentials") && !clientId) {
    throw new ProfileError(`${where}.clientId is required for '${type}' sign-in.`);
  }

  const username = spec.username === undefined ? undefined : requireString(spec.username, `${where}.username`);
  if (type === "password" && !username) {
    throw new ProfileError(`${where}.username is required for 'password' sign-in.`);
  }

  const ports = spec.callbackPorts ?? (spec.callbackPort === undefined ? undefined : [spec.callbackPort]);
  const callbackPorts = ports === undefined ? undefined : parsePorts(ports, `${where}.callbackPorts`);

  return {
    type,
    ...(clientId ? { clientId } : {}),
    ...(spec.authority === undefined ? {} : { authority: parseUrl(requireString(spec.authority, `${where}.authority`), `${where}.authority`) }),
    ...(spec.scope === undefined ? {} : { scope: requireString(spec.scope, `${where}.scope`) }),
    ...(username ? { username } : {}),
    ...(callbackPorts ? { callbackPorts } : {}),
    ...(spec.redirectHost === undefined ? {} : { redirectHost: optionalEnum(spec.redirectHost, ["127.0.0.1", "localhost"] as const, `${where}.redirectHost`)! }),
    ...(spec.tokenEnv === undefined ? {} : { tokenEnv: requireString(spec.tokenEnv, `${where}.tokenEnv`) }),
    ...(spec.tokenFile === undefined ? {} : { tokenFile: requireString(spec.tokenFile, `${where}.tokenFile`) }),
  };
}

/**
 * A profile defined entirely by environment variables (`ABP_MCP_URL`, `ABP_MCP_AUTH`, ...). This is how the
 * Claude Desktop extension passes its settings, so it works without a profiles file.
 */
export function profileFromEnv(name: string, env: NodeJS.ProcessEnv): Profile | undefined {
  if (!env.ABP_MCP_URL) {
    return undefined;
  }

  const pick = (key: string): string | undefined => (env[key] && env[key]!.trim() !== "" ? env[key]!.trim() : undefined);
  const auth: Record<string, unknown> = { type: pick("ABP_MCP_AUTH") ?? "none" };
  for (const [key, variable] of [["clientId", "ABP_MCP_CLIENT_ID"], ["authority", "ABP_MCP_AUTHORITY"], ["scope", "ABP_MCP_SCOPE"], ["username", "ABP_MCP_USERNAME"]] as const) {
    const value = pick(variable);
    if (value) {
      auth[key] = value;
    }
  }

  const tools: Record<string, unknown> = {};
  const preset = pick("ABP_MCP_TOOLS_PRESET");
  if (preset) {
    tools.preset = preset;
  }

  const notes = pick("ABP_MCP_NOTES");
  return parseProfile(name, {
    url: env.ABP_MCP_URL,
    stage: pick("ABP_MCP_STAGE") ?? "development",
    auth,
    tools,
    ...(notes ? { notes } : {}),
  });
}

/** Read and validate the profiles file. A missing file is an empty set of profiles. */
export function loadProfiles(file: string = profilesPath()): Map<string, Profile> {
  let text: string;
  try {
    text = fs.readFileSync(file, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return new Map();
    }

    throw error;
  }

  let document: unknown;
  try {
    document = JSON.parse(text);
  } catch (error) {
    throw new ProfileError(`${file} is not valid JSON: ${(error as Error).message}`);
  }

  return parseProfiles(document);
}

/** The named profile: from the profiles file, else from `ABP_MCP_*` environment variables. */
export function resolveProfile(name: string, env: NodeJS.ProcessEnv = process.env): Profile {
  const file = profilesPath(env);
  const profiles = loadProfiles(file);
  const profile = profiles.get(name) ?? profileFromEnv(name, env);
  if (!profile) {
    const known = [...profiles.keys()];
    throw new ProfileError(
      `No profile named '${name}' in ${file}` +
      (known.length > 0 ? ` (known: ${known.join(", ")})` : "") +
      ". Add one with `abp-mcp profiles add " + name + "`.");
  }

  return profile;
}

/** Read the raw profiles document, for commands that edit it. */
export function readProfilesDocument(file: string): { profiles: Record<string, unknown> } {
  if (!fs.existsSync(file)) {
    return { profiles: {} };
  }

  const document = JSON.parse(fs.readFileSync(file, "utf8")) as { profiles?: Record<string, unknown> };
  return { ...document, profiles: document.profiles ?? {} };
}

/** Write the profiles document after validating it, so a bad edit never lands on disk. */
export function writeProfilesDocument(file: string, document: { profiles: Record<string, unknown> }): void {
  parseProfiles(document);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(document, null, 2) + "\n");
}

function asObject(value: unknown, where: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new ProfileError(`${where} must be an object.`);
  }

  return value as Record<string, unknown>;
}

function rejectUnknown(spec: Record<string, unknown>, known: ReadonlySet<string>, where: string): void {
  for (const key of Object.keys(spec)) {
    if (!known.has(key)) {
      throw new ProfileError(`Unknown key '${where}.${key}'. Expected one of: ${[...known].join(", ")}.`);
    }
  }
}

function requireString(value: unknown, where: string): string {
  if (typeof value !== "string" || value.trim() === "") {
    throw new ProfileError(`${where} must be a non-empty string.`);
  }

  return value.trim();
}

function requirePositiveNumber(value: unknown, where: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) {
    throw new ProfileError(`${where} must be a positive number.`);
  }

  return value;
}

function optionalEnum<T extends string>(value: unknown, allowed: readonly T[], where: string): T | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== "string" || !allowed.includes(value as T)) {
    throw new ProfileError(`${where} must be one of: ${allowed.join(", ")}.`);
  }

  return value as T;
}

function optionalStringArray(value: unknown, where: string): string[] {
  if (value === undefined) {
    return [];
  }

  if (!Array.isArray(value) || value.some((item) => typeof item !== "string" || item.trim() === "")) {
    throw new ProfileError(`${where} must be an array of non-empty strings.`);
  }

  return value.map((item: string) => item.trim());
}

function parsePorts(value: unknown, where: string): number[] {
  const ports = Array.isArray(value) ? value : [value];
  if (ports.length === 0 || ports.some((port) => !Number.isInteger(port) || port < 1024 || port > 65535)) {
    throw new ProfileError(`${where} must list ports between 1024 and 65535.`);
  }

  return ports as number[];
}

/** HTTPS only, except for loopback hosts, so a token never crosses the network in clear text. */
function parseUrl(value: string, where: string): string {
  try {
    new URL(value);
  } catch {
    throw new ProfileError(`${where} is not a valid URL: '${value}'.`);
  }

  if (urlPolicyViolation(value)) {
    throw new ProfileError(`${where} must use https (plain http is allowed only for localhost): '${value}'.`);
  }

  return value.replace(/\/+$/, "");
}
