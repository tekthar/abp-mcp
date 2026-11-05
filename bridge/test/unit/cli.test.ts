import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { main } from "../../src/cli.js";
import type { Io } from "../../src/io.js";
import { tempDir } from "../helpers/harness.js";

function recordingIo(): Io & { lines: string[] } {
  const lines: string[] = [];
  return {
    lines,
    out: (line) => lines.push(line),
    err: (line) => lines.push(line),
    prompt: async () => {
      throw new Error("unexpected prompt");
    },
    promptHidden: async () => {
      throw new Error("unexpected prompt");
    },
  };
}

describe("cli", () => {
  it("adds, lists and removes a profile without prompting when the flags say everything", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const env = { ABP_MCP_PROFILES: path.join(dir, "profiles.json"), ABP_MCP_CONFIG_DIR: dir, ABP_MCP_KEYCHAIN: "off" };
      const io = recordingIo();
      assert.equal(await main(["profiles", "add", "prod", "--url", "https://api.example.com/mcp", "--stage", "production",
        "--auth", "browser", "--client-id", "MyApp_Mcp", "--authority", "https://auth.example.com", "--scope", "openid offline_access"], io, env), 0);
      assert.match(io.lines.join("\n"), /read-only tools unless tools\.preset is 'all'/);

      const saved = JSON.parse(fs.readFileSync(env.ABP_MCP_PROFILES, "utf8")) as { profiles: Record<string, { auth: { clientId: string } }> };
      assert.equal(saved.profiles.prod?.auth.clientId, "MyApp_Mcp");

      await main(["profiles", "list"], io, env);
      assert.match(io.lines.join("\n"), /prod {2}https:\/\/api\.example\.com\/mcp {2}production, browser sign-in, read-only tools/);

      await main(["profiles", "remove", "prod"], io, env);
      assert.deepEqual(JSON.parse(fs.readFileSync(env.ABP_MCP_PROFILES, "utf8")), { profiles: {} });
    } finally {
      cleanup();
    }
  });

  it("install refuses unknown profiles and names a shell could misread", async () => {
    const { dir, cleanup } = tempDir();
    try {
      const env = { ABP_MCP_PROFILES: path.join(dir, "profiles.json") };
      fs.writeFileSync(env.ABP_MCP_PROFILES, JSON.stringify({ profiles: { dev: { url: "http://localhost:5000/mcp", auth: { type: "none" } } } }));
      await assert.rejects(main(["install", "nope", "--claude-desktop"], recordingIo(), env), /not a profile/);
      await assert.rejects(main(["install", "dev", "--claude-desktop", "--name", "dev & calc"], recordingIo(), env), /not a valid name/);
    } finally {
      cleanup();
    }
  });

  it("prints the version and rejects unknown commands", async () => {
    const io = recordingIo();
    assert.equal(await main(["--version"], io, {}), 0);
    assert.match(io.lines[0] ?? "", /^\d+\.\d+\.\d+$/);
    await assert.rejects(main(["frobnicate"], io, {}), /Unknown command 'frobnicate'/);
  });
});
