import { parseArgs } from "node:util";
import { doctor } from "./commands/doctor.js";
import { installClaudeCode, installClaudeDesktop } from "./commands/install.js";
import { login, logout } from "./commands/login.js";
import { addProfile, listProfiles, removeProfile } from "./commands/profiles.js";
import { runStdio } from "./commands/run.js";
import { terminalIo, type Io } from "./io.js";
import { describeError } from "./log.js";
import { profilesPath } from "./paths.js";
import { loadProfiles } from "./profiles.js";
import { createRuntime, packageVersion } from "./runtime.js";

const HELP = `abp-mcp-bridge: connect Claude to an AbpMcp endpoint, signed in as you.

Usage:
  abp-mcp-bridge --profile <name>                 Serve the profile over stdio (what Claude runs)
  abp-mcp-bridge login <profile> [--save-password | --save-secret]
                                                  Sign in now and keep the sign-in
  abp-mcp-bridge logout <profile>                 Forget the stored sign-in
  abp-mcp-bridge profiles list
  abp-mcp-bridge profiles add <name> [--url <url>] [--stage development|staging|production]
                 [--auth browser|password|client_credentials|token|none] [--client-id <id>]
                 [--authority <url>] [--scope "<scopes>"] [--username <name>]
                 [--callback-ports 33418,33419] [--preset all|read-only] [--notes "<text>"]
  abp-mcp-bridge profiles remove <name>
  abp-mcp-bridge doctor <profile> [--tools]       Check every step up to tools/list
  abp-mcp-bridge install <profile> --claude-code [--scope user|project|local] [--name <name>]
  abp-mcp-bridge install <profile> --claude-desktop [--name <name>]

Profiles live in %APPDATA%\\abp-mcp\\profiles.json (Windows) or ~/.config/abp-mcp/profiles.json.

Environment:
  ABP_MCP_PROFILES        Path of the profiles file
  ABP_MCP_PASSWORD        Password for password profiles (never saved unless --save-password)
  ABP_MCP_CLIENT_SECRET   Client secret for client_credentials profiles
  ABP_MCP_TOKEN           Bearer token for token profiles (or auth.tokenEnv)
  ABP_MCP_URL, ABP_MCP_AUTH, ABP_MCP_CLIENT_ID, ABP_MCP_AUTHORITY, ABP_MCP_SCOPE,
  ABP_MCP_USERNAME, ABP_MCP_STAGE, ABP_MCP_TOOLS_PRESET, ABP_MCP_NOTES
                          Define the profile without a profiles file
  ABP_MCP_DEBUG=1         Debug logging (secrets are always redacted)
  ABP_MCP_LOG_FILE        Also write the log to this file
  ABP_MCP_AUDIT=1         Record each tool call (no arguments or results)
  ABP_MCP_KEYCHAIN=off    Do not use the OS keychain
`;

/** Run the CLI. Returns the process exit code; stdio mode keeps running after it returns. */
export async function main(argv: string[], io: Io = terminalIo, env: NodeJS.ProcessEnv = process.env): Promise<number> {
  const { values, positionals } = parseArgs({
    args: argv,
    allowPositionals: true,
    strict: true,
    options: {
      profile: { type: "string", short: "p" },
      help: { type: "boolean", short: "h" },
      version: { type: "boolean", short: "v" },
      "save-password": { type: "boolean" },
      "save-secret": { type: "boolean" },
      tools: { type: "boolean" },
      "claude-code": { type: "boolean" },
      "claude-desktop": { type: "boolean" },
      scope: { type: "string" },
      name: { type: "string" },
      url: { type: "string" },
      stage: { type: "string" },
      auth: { type: "string" },
      "client-id": { type: "string" },
      authority: { type: "string" },
      username: { type: "string" },
      "callback-ports": { type: "string" },
      preset: { type: "string" },
      notes: { type: "string" },
    },
  });

  const [command, ...rest] = positionals;
  if (values.version) {
    io.out(packageVersion());
    return 0;
  }

  if (values.help || (!command && !values.profile)) {
    io.out(HELP);
    return values.help ? 0 : 1;
  }

  const target = (): string => {
    const name = rest[0] ?? values.profile;
    if (!name) {
      throw new UsageError(`'${command}' needs a profile name.`);
    }

    return name;
  };

  switch (command) {
    case undefined:
    case "run":
      await runStdio(await createRuntime(values.profile ?? target(), { env }));
      return -1;

    case "login":
      await login(await createRuntime(target(), { env }), { save: Boolean(values["save-password"] || values["save-secret"]) }, io);
      return 0;

    case "logout":
      await logout(await createRuntime(target(), { env }), io);
      return 0;

    case "doctor":
      return await doctor(await createRuntime(target(), { env }), { tools: Boolean(values.tools) }, io) ? 0 : 1;

    case "profiles": {
      const [action, name] = rest;
      if (action === "list" || action === undefined) {
        listProfiles(env, io);
        return 0;
      }

      if (!name) {
        throw new UsageError(`'profiles ${action}' needs a profile name.`);
      }

      if (action === "add") {
        await addProfile(name, {
          url: values.url, stage: values.stage, auth: values.auth, clientId: values["client-id"],
          authority: values.authority, scope: values.scope, username: values.username,
          callbackPorts: values["callback-ports"], preset: values.preset, notes: values.notes,
          interactive: process.stdin.isTTY === true,
        }, env, io);
        return 0;
      }

      if (action === "remove") {
        await removeProfile(name, env, io);
        return 0;
      }

      throw new UsageError(`Unknown action 'profiles ${action}'.`);
    }

    case "install": {
      const profile = target();
      const name = values.name ?? profile;
      // Both end up on a command line (through a shell on Windows): only plain names.
      if (!SERVER_NAME.test(name) || !loadProfiles(profilesPath(env)).has(profile)) {
        throw new UsageError(`'${profile}' is not a profile in ${profilesPath(env)}, or '${name}' is not a valid name (letters, digits, '.', '_', '-').`);
      }
      if (values["claude-code"]) {
        return installClaudeCode(profile, { name, scope: values.scope ?? "user" }, io) ? 0 : 1;
      }

      if (values["claude-desktop"]) {
        installClaudeDesktop(profile, { name }, io);
        return 0;
      }

      throw new UsageError("'install' needs --claude-code or --claude-desktop.");
    }

    default:
      throw new UsageError(`Unknown command '${command}'.`);
  }
}

class UsageError extends Error {}

const SERVER_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/** Run the CLI for this process: what `bin.ts` calls. */
export async function runCli(): Promise<void> {
  try {
    const code = await main(process.argv.slice(2));
    if (code >= 0) {
      process.exitCode = code;
    }
  } catch (error) {
    terminalIo.err(`abp-mcp-bridge: ${describeError(error)}`);
    if (error instanceof UsageError || (error as { code?: unknown }).code === "ERR_PARSE_ARGS_UNKNOWN_OPTION") {
      terminalIo.err("Run `abp-mcp-bridge --help` for usage.");
    }

    process.exitCode = 1;
  }
}
