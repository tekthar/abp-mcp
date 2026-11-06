import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { describe, it } from "node:test";
import { SignInBreaker } from "../../src/auth/breaker.js";
import { NotSignedInError } from "../../src/auth/errors.js";
import { TokenManager } from "../../src/auth/token-manager.js";
import { CredentialVault, FileSecretStore } from "../../src/auth/vault.js";
import { Logger } from "../../src/log.js";
import { configDir, profilesPath, stateDir } from "../../src/paths.js";
import { parseProfile } from "../../src/profiles.js";
import { Redactor } from "../../src/redact.js";
import { tempDir } from "../helpers/harness.js";

describe("paths", () => {
  it("uses %APPDATA%\\abp-mcp on Windows, and AppData\\Roaming without APPDATA", () => {
    assert.equal(configDir({ APPDATA: "C:\\Users\\a\\AppData\\Roaming" }, "win32"), path.join("C:\\Users\\a\\AppData\\Roaming", "abp-mcp"));
    assert.equal(configDir({}, "win32"), path.join(os.homedir(), "AppData", "Roaming", "abp-mcp"));
  });

  it("uses $XDG_CONFIG_HOME/abp-mcp, else ~/.config/abp-mcp, on macOS and Linux", () => {
    for (const platform of ["linux", "darwin"] as const) {
      assert.equal(configDir({ XDG_CONFIG_HOME: "/xdg" }, platform), path.join("/xdg", "abp-mcp"));
      assert.equal(configDir({}, platform), path.join(os.homedir(), ".config", "abp-mcp"));
    }
  });

  it("lets ABP_MCP_CONFIG_DIR win on every platform", () => {
    for (const platform of ["win32", "linux", "darwin"] as const) {
      assert.equal(configDir({ ABP_MCP_CONFIG_DIR: "/custom", APPDATA: "C:\\x", XDG_CONFIG_HOME: "/xdg" }, platform), "/custom");
    }
  });

  it("puts profiles.json and state/ in the config directory, and ABP_MCP_PROFILES overrides the file", () => {
    assert.equal(profilesPath({ ABP_MCP_CONFIG_DIR: "/c" }), path.join("/c", "profiles.json"));
    assert.equal(profilesPath({ ABP_MCP_CONFIG_DIR: "/c", ABP_MCP_PROFILES: "/elsewhere/p.json" }), "/elsewhere/p.json");
    assert.equal(stateDir({ ABP_MCP_CONFIG_DIR: "/c" }), path.join("/c", "state"));
  });
});

function restoreEnv(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
  } else {
    process.env[name] = value;
  }
}

describe("auth.tokenFile", () => {
  function tokensFor(dir: string, tokenFile: string): TokenManager {
    const redactor = new Redactor();
    return new TokenManager({
      profile: parseProfile("t", { url: "http://localhost:5000/mcp", auth: { type: "token", tokenFile } }),
      vault: new CredentialVault(undefined, new FileSecretStore(path.join(dir, "credentials"))),
      breaker: SignInBreaker.forProfile(dir, "t"),
      log: new Logger({ redactor, sink: () => undefined }),
      redactor,
      env: {},
    });
  }

  it("reads the token from the file, trimmed", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = path.join(dir, "token.txt");
      fs.writeFileSync(file, "  file-token-123\n");
      assert.equal(await tokensFor(dir, file).getAccessToken({ interactive: false }), "file-token-123");
    } finally {
      cleanup();
    }
  });

  it("expands a leading ~ to the home directory", async () => {
    const { dir, cleanup } = tempDir();
    const home = process.env.HOME;
    const userProfile = process.env.USERPROFILE;
    try {
      process.env.HOME = dir;
      process.env.USERPROFILE = dir;
      fs.writeFileSync(path.join(dir, "token.txt"), "home-token-456");
      assert.equal(await tokensFor(dir, "~/token.txt").getAccessToken({ interactive: false }), "home-token-456");
    } finally {
      restoreEnv("HOME", home);
      restoreEnv("USERPROFILE", userProfile);
      cleanup();
    }
  });

  it("names the file when it is missing or empty", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const missing = path.join(dir, "missing.txt");
      await assert.rejects(tokensFor(dir, missing).getAccessToken({ interactive: false }),
        (error: unknown) => error instanceof NotSignedInError && error.message.includes(missing));

      const empty = path.join(dir, "empty.txt");
      fs.writeFileSync(empty, "\n");
      await assert.rejects(tokensFor(dir, empty).getAccessToken({ interactive: false }), /missing or empty/);
    } finally {
      cleanup();
    }
  });
});
