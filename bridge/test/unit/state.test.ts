import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";
import { AuditLog, toolErrorCode } from "../../src/audit.js";
import { BREAKER_PAUSE_MS, SignInBreaker } from "../../src/auth/breaker.js";
import { BreakerOpenError, KeychainUnavailableError } from "../../src/auth/errors.js";
import { CredentialVault, FileSecretStore } from "../../src/auth/vault.js";
import { claudeDesktopConfigPath, installClaudeDesktop } from "../../src/commands/install.js";
import { classifyCallError, MayHaveRunError, NotSentError } from "../../src/upstream/errors.js";
import { MemoryKeychain, tempDir } from "../helpers/harness.js";

const silentIo = { out: () => undefined, err: () => undefined, prompt: async () => "", promptHidden: async () => "" };

describe("sign-in breaker", () => {
  it("pauses for 15 minutes, survives a restart, and resets", () => {
    const { dir, cleanup } = tempDir();
    try {
      let now = 1_000_000;
      const breaker = SignInBreaker.forProfile(dir, "prod", () => now);
      breaker.check();
      breaker.trip("invalid_grant");

      const restarted = SignInBreaker.forProfile(dir, "prod", () => now);
      assert.throws(() => restarted.check(), BreakerOpenError);
      assert.equal(restarted.status().open, true);

      now += BREAKER_PAUSE_MS + 1;
      restarted.check();
      restarted.trip("invalid_client");
      restarted.reset();
      restarted.check();
    } finally {
      cleanup();
    }
  });
});

describe("credential vault", () => {
  it("keeps refresh tokens in an owner-only file when there is no keychain", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const vault = new CredentialVault(undefined, new FileSecretStore(path.join(dir, "credentials")));
      await vault.setRefreshToken("dev", "rt-value");
      assert.equal(await vault.getRefreshToken("dev"), "rt-value");
      const file = path.join(dir, "credentials", "dev.json");
      if (process.platform !== "win32") {
        assert.equal(fs.statSync(file).mode & 0o777, 0o600);
      }

      await vault.deleteRefreshToken("dev");
      assert.equal(fs.existsSync(file), false, "an empty file is removed");
    } finally {
      cleanup();
    }
  });

  it("never writes a password to a file", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const vault = new CredentialVault(undefined, new FileSecretStore(path.join(dir, "credentials")));
      await assert.rejects(vault.setSecret("dev", "password", "pw"), KeychainUnavailableError);
      assert.equal(await vault.getSecret("dev", "password"), null);
      assert.equal(fs.existsSync(path.join(dir, "credentials")), false);
    } finally {
      cleanup();
    }
  });

  it("prefers the keychain and moves a file-stored refresh token into it", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = new FileSecretStore(path.join(dir, "credentials"));
      await file.set("dev", "refresh_token", "old-file-token");
      const keychain = new MemoryKeychain();
      const vault = new CredentialVault(keychain, file);

      await vault.setRefreshToken("dev", "new-token");
      assert.equal(await vault.getRefreshToken("dev"), "new-token");
      assert.equal(await file.get("dev", "refresh_token"), null);

      await vault.setSecret("dev", "password", "pw");
      await vault.forget("dev");
      assert.equal(keychain.values.size, 0);
    } finally {
      cleanup();
    }
  });
});

describe("call error classification", () => {
  it("passes the host's JSON-RPC errors through", () => {
    const error = new McpError(ErrorCode.InvalidParams, "Unknown tool");
    assert.equal(classifyCallError(error, "t", "h"), error);
  });

  it("reports a timeout or a dropped connection as possibly run", () => {
    assert.ok(classifyCallError(new McpError(ErrorCode.RequestTimeout, "timeout"), "t", "h") instanceof MayHaveRunError);
    assert.ok(classifyCallError(new TypeError("fetch failed", { cause: { code: "UND_ERR_SOCKET" } }), "t", "h") instanceof MayHaveRunError);
    assert.ok(classifyCallError(new StreamableHTTPError(502, "bad gateway"), "t", "h") instanceof MayHaveRunError);
  });

  it("reports a connection that never opened as not sent", () => {
    const error = classifyCallError(new TypeError("fetch failed", { cause: { code: "ECONNREFUSED" } }), "t", "api.example.com");
    assert.ok(error instanceof NotSentError);
    assert.match(error.message, /did not run/);
  });

  it("keeps a refusal before dispatch (4xx) as is", () => {
    const error = new StreamableHTTPError(403, "forbidden");
    assert.equal(classifyCallError(error, "t", "h"), error);
  });
});

describe("audit log", () => {
  it("records outcomes without arguments or results, and rotates", () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = path.join(dir, "audit", "p.jsonl");
      const audit = new AuditLog(file, "p");
      audit.record({ tool: "Book_Get", outcome: "ok", durationMs: 5 });
      const entry = JSON.parse(fs.readFileSync(file, "utf8").trim()) as Record<string, unknown>;
      assert.deepEqual(Object.keys(entry).sort(), ["durationMs", "outcome", "profile", "time", "tool"]);

      fs.writeFileSync(file, "x".repeat(5 * 1024 * 1024));
      audit.record({ tool: "Book_Get", outcome: "tool_error", durationMs: 1, errorCode: "FORBIDDEN" });
      assert.ok(fs.existsSync(`${file}.1`));
      assert.match(fs.readFileSync(file, "utf8"), /FORBIDDEN/);
    } finally {
      cleanup();
    }
  });

  it("reads the AbpMcp error code of a tool error", () => {
    const result: CallToolResult = { isError: true, content: [{ type: "text", text: "[LIBRARY:MEMBER_SUSPENDED] Member is suspended." }] };
    assert.equal(toolErrorCode(result), "LIBRARY:MEMBER_SUSPENDED");
    assert.equal(toolErrorCode({ content: [{ type: "text", text: "no code" }] }), undefined);
  });
});

describe("install", () => {
  it("adds the profile to Claude Desktop's config and keeps a backup", () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = path.join(dir, "claude_desktop_config.json");
      fs.writeFileSync(file, JSON.stringify({ mcpServers: { other: { command: "x" } }, theme: "dark" }));
      installClaudeDesktop("staging", { name: "library-staging", configPath: file }, silentIo);

      const config = JSON.parse(fs.readFileSync(file, "utf8")) as { mcpServers: Record<string, { command: string; args: string[] }>; theme: string };
      assert.equal(config.theme, "dark");
      assert.ok(config.mcpServers.other);
      assert.deepEqual(config.mcpServers["library-staging"], { command: "npx", args: ["-y", "@tekthar/abp-mcp-bridge", "--profile", "staging"] });
      assert.equal(fs.readdirSync(dir).filter((name) => name.includes(".bak-")).length, 1);
    } finally {
      cleanup();
    }
  });

  it("knows where Claude Desktop keeps its config", () => {
    assert.match(claudeDesktopConfigPath({ APPDATA: "C:\\Users\\a\\AppData\\Roaming" }, "win32"), /Claude[\\/]claude_desktop_config\.json$/);
    assert.match(claudeDesktopConfigPath({}, "darwin"), /Library[\\/]Application Support[\\/]Claude/);
  });
});
