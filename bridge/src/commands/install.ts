import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import type { Io } from "../io.js";

export const PACKAGE_NAME = "abp-mcp";

/** The command Claude runs to start the bridge for a profile. */
export function launchCommand(profile: string): { command: string; args: string[] } {
  return { command: "npx", args: ["-y", PACKAGE_NAME, "--profile", profile] };
}

/** Register the profile with Claude Code through `claude mcp add`. */
export function installClaudeCode(profile: string, options: { name: string; scope: string }, io: Io): boolean {
  const launch = launchCommand(profile);
  const args = ["mcp", "add", options.name, "--scope", options.scope, "--", launch.command, ...launch.args];
  const result = spawnSync("claude", args, { stdio: "inherit", shell: process.platform === "win32" });
  if (result.error || result.status !== 0) {
    io.err("Could not run `claude`. Run this yourself:");
    io.err(`  claude ${args.join(" ")}`);
    return false;
  }

  io.out(`Added '${options.name}' to Claude Code (${options.scope} scope).`);
  return true;
}

/** Where Claude Desktop keeps its configuration on this platform. */
export function claudeDesktopConfigPath(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string {
  if (platform === "win32") {
    return path.join(env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"), "Claude", "claude_desktop_config.json");
  }

  if (platform === "darwin") {
    return path.join(os.homedir(), "Library", "Application Support", "Claude", "claude_desktop_config.json");
  }

  return path.join(env.XDG_CONFIG_HOME ?? path.join(os.homedir(), ".config"), "Claude", "claude_desktop_config.json");
}

/** Add the profile to Claude Desktop's configuration, after backing the file up. */
export function installClaudeDesktop(profile: string, options: { name: string; configPath?: string }, io: Io): void {
  const file = options.configPath ?? claudeDesktopConfigPath();
  let config: { mcpServers?: Record<string, unknown> } = {};
  if (fs.existsSync(file)) {
    const text = fs.readFileSync(file, "utf8");
    config = text.trim() === "" ? {} : JSON.parse(text) as typeof config;
    const backup = `${file}.bak-${new Date().toISOString().replace(/[:.]/g, "-")}`;
    fs.copyFileSync(file, backup);
    io.out(`Backed up ${file} to ${backup}.`);
  } else {
    fs.mkdirSync(path.dirname(file), { recursive: true });
  }

  config.mcpServers = { ...config.mcpServers, [options.name]: launchCommand(profile) };
  fs.writeFileSync(file, JSON.stringify(config, null, 2) + "\n");
  io.out(`Added '${options.name}' to ${file}. Restart Claude Desktop to load it.`);
}
