// Builds abp-mcp-bridge-<version>.mcpb, the one-click Claude Desktop extension.
// Run `npm run build` first (`npm run pack:mcpb` does both).
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const staging = path.join(root, "mcpb-staging");
const manifestPackage = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const { version } = manifestPackage;
const npm = process.platform === "win32" ? "npm.cmd" : "npm";

// Prebuilt keychain binaries for every desktop platform Claude Desktop runs on, so one bundle works on all
// of them. Without one, the bridge keeps refresh tokens in an owner-only file instead.
const keyringVersion = manifestPackage.optionalDependencies["@napi-rs/keyring"];
const keyringPlatforms = ["win32-x64-msvc", "win32-arm64-msvc", "darwin-x64", "darwin-arm64", "linux-x64-gnu", "linux-arm64-gnu"];

if (!fs.existsSync(path.join(root, "dist", "bin.js"))) {
  throw new Error("dist/bin.js is missing: run `npm run build` first.");
}

fs.rmSync(staging, { recursive: true, force: true });
fs.mkdirSync(staging, { recursive: true });
fs.cpSync(path.join(root, "dist"), path.join(staging, "dist"), { recursive: true });
for (const file of ["README.md", "LICENSE"]) {
  fs.copyFileSync(path.join(root, file), path.join(staging, file));
}

fs.writeFileSync(path.join(staging, "package.json"), JSON.stringify({
  name: manifestPackage.name,
  version,
  type: "module",
  private: true,
  dependencies: manifestPackage.dependencies,
  optionalDependencies: manifestPackage.optionalDependencies,
}, null, 2));
fs.copyFileSync(path.join(root, "package-lock.json"), path.join(staging, "package-lock.json"));
execFileSync(npm, ["ci", "--omit=dev", "--no-audit", "--no-fund"], { cwd: staging, stdio: "inherit", shell: process.platform === "win32" });

const binaries = path.join(staging, "node_modules", "@napi-rs");
const downloads = fs.mkdtempSync(path.join(root, ".keyring-"));
try {
  for (const platform of keyringPlatforms) {
    const name = `@napi-rs/keyring-${platform}`;
    const target = path.join(binaries, `keyring-${platform}`);
    if (fs.existsSync(target)) {
      continue;
    }

    const tarball = execFileSync(npm, ["pack", `${name}@${keyringVersion}`, "--silent"], { cwd: downloads, encoding: "utf8", shell: process.platform === "win32" }).trim().split("\n").at(-1);
    fs.mkdirSync(target, { recursive: true });
    execFileSync("tar", ["-xzf", path.join(downloads, tarball), "-C", target, "--strip-components=1"]);
  }
} finally {
  fs.rmSync(downloads, { recursive: true, force: true });
}

const setting = (title, description, extra = {}) => ({ type: "string", title, description, required: false, ...extra });
const manifest = {
  manifest_version: "0.3",
  name: "abp-mcp-bridge",
  display_name: "ABP app (AbpMcp)",
  version,
  description: "Connect Claude to an ABP Framework app that uses AbpMcp, signed in as you.",
  long_description:
    "Signs you in to an ABP application (browser sign-in, or a password where the app allows it), keeps the " +
    "sign-in fresh, and gives Claude the app's MCP tools, filtered by your permissions. Production " +
    "connections are read-only unless you choose otherwise. Works with apps on your intranet or VPN.",
  author: { name: "tekthar", url: "https://github.com/tekthar" },
  repository: { type: "git", url: "https://github.com/tekthar/abp-mcp" },
  homepage: "https://github.com/tekthar/abp-mcp/tree/main/bridge",
  documentation: "https://github.com/tekthar/abp-mcp/blob/main/docs/bridge.md",
  support: "https://github.com/tekthar/abp-mcp/issues",
  license: "MIT",
  keywords: ["abp", "mcp", "openiddict"],
  server: {
    type: "node",
    entry_point: "dist/bin.js",
    mcp_config: {
      command: "node",
      args: ["${__dirname}/dist/bin.js", "--profile", "${user_config.profile}"],
      env: {
        ABP_MCP_URL: "${user_config.url}",
        ABP_MCP_STAGE: "${user_config.stage}",
        ABP_MCP_AUTH: "${user_config.auth}",
        ABP_MCP_CLIENT_ID: "${user_config.client_id}",
        ABP_MCP_AUTHORITY: "${user_config.authority}",
        ABP_MCP_SCOPE: "${user_config.scope}",
        ABP_MCP_USERNAME: "${user_config.username}",
        ABP_MCP_PASSWORD: "${user_config.password}",
        ABP_MCP_TOOLS_PRESET: "${user_config.tools_preset}",
      },
    },
  },
  compatibility: { platforms: ["darwin", "win32", "linux"], runtimes: { node: ">=22.0.0" } },
  user_config: {
    url: setting("MCP endpoint", "The app's AbpMcp endpoint, e.g. https://api.example.com/mcp.", { required: true }),
    stage: setting("Stage", "development, staging or production. Production is read-only unless Tools is 'all'.", { default: "development" }),
    auth: setting("Sign-in", "browser (recommended), password (deprecated, for apps without browser sign-in), or none.", { default: "browser" }),
    client_id: setting("Client id", "The app's OpenIddict client id, e.g. MyApp_App."),
    authority: setting("Authorization server", "Leave empty to discover it from the app."),
    scope: setting("Scopes", "Leave empty to use what the app advertises, e.g. 'openid offline_access MyApp'."),
    username: setting("User name", "For password sign-in."),
    password: setting("Password", "For password sign-in. Kept in your OS keychain by Claude Desktop.", { sensitive: true }),
    tools_preset: setting("Tools", "all or read-only. Empty: read-only for production, all otherwise."),
    profile: setting("Profile", "Advanced: a profile name from profiles.json. Its settings win over the ones above.", { default: "claude-desktop" }),
  },
};

fs.writeFileSync(path.join(staging, "manifest.json"), JSON.stringify(manifest, null, 2));

const mcpb = path.join(root, "node_modules", ".bin", process.platform === "win32" ? "mcpb.cmd" : "mcpb");
const output = path.join(root, `abp-mcp-bridge-${version}.mcpb`);
execFileSync(mcpb, ["validate", path.join(staging, "manifest.json")], { stdio: "inherit", shell: process.platform === "win32" });
execFileSync(mcpb, ["pack", staging, output], { stdio: "inherit", shell: process.platform === "win32" });
console.log(`Built ${path.relative(root, output)}`);
