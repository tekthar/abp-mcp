import os from "node:os";
import path from "node:path";

/** Where the bridge keeps its configuration: `%APPDATA%\abp-mcp` on Windows, `$XDG_CONFIG_HOME/abp-mcp` (or `~/.config/abp-mcp`) elsewhere. */
export function configDir(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string {
  if (env.ABP_MCP_CONFIG_DIR) {
    return env.ABP_MCP_CONFIG_DIR;
  }

  if (platform === "win32") {
    return path.join(env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"), "abp-mcp");
  }

  return path.join(env.XDG_CONFIG_HOME ?? path.join(os.homedir(), ".config"), "abp-mcp");
}

/** The profiles file. `ABP_MCP_PROFILES` overrides it. */
export function profilesPath(env: NodeJS.ProcessEnv = process.env): string {
  return env.ABP_MCP_PROFILES ?? path.join(configDir(env), "profiles.json");
}

/** Per-profile state the bridge writes itself: the sign-in breaker, file-stored refresh tokens, audit logs. */
export function stateDir(env: NodeJS.ProcessEnv = process.env): string {
  return path.join(configDir(env), "state");
}
