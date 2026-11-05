import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { loadProfiles, parseProfile, parseProfiles, profileFromEnv, ProfileError, resolveProfile, writeProfilesDocument } from "../../src/profiles.js";
import { tempDir } from "../helpers/harness.js";

describe("profiles", () => {
  it("applies defaults: development stage, all tools, 300 s timeout", () => {
    const profile = parseProfile("dev", { url: "https://api.example.com/mcp/", auth: { type: "none" } });
    assert.equal(profile.stage, "development");
    assert.equal(profile.tools.preset, "all");
    assert.equal(profile.timeoutSeconds, 300);
    assert.equal(profile.url, "https://api.example.com/mcp", "a trailing slash is trimmed");
  });

  it("makes production read-only unless the profile asks for more", () => {
    const readOnly = parseProfile("prod", { url: "https://api.example.com/mcp", stage: "production", auth: { type: "none" } });
    assert.equal(readOnly.tools.preset, "read-only");

    const writable = parseProfile("prod", { url: "https://api.example.com/mcp", stage: "production", auth: { type: "none" }, tools: { preset: "all" } });
    assert.equal(writable.tools.preset, "all");
  });

  it("names the offending key", () => {
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "browser" } }), /profiles\.p\.auth\.clientId is required/);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "password", clientId: "c" } }), /profiles\.p\.auth\.username is required/);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "none" }, colour: "red" }), /Unknown key 'profiles\.p\.colour'/);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "none", secret: "x" } }), /Unknown key 'profiles\.p\.auth\.secret'/);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", stage: "prod", auth: { type: "none" } }), /profiles\.p\.stage must be one of/);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "none" }, tools: { deny: "x" } }), /tools\.deny must be an array/);
    assert.throws(() => parseProfile("bad name", { url: "https://x/mcp", auth: { type: "none" } }), ProfileError);
  });

  it("refuses plain http except for loopback hosts", () => {
    assert.throws(() => parseProfile("p", { url: "http://api.example.com/mcp", auth: { type: "none" } }), /must use https/);
    for (const host of ["localhost:5000", "127.0.0.1:5000", "[::1]:5000"]) {
      assert.equal(parseProfile("p", { url: `http://${host}/mcp`, auth: { type: "none" } }).url, `http://${host}/mcp`);
    }
  });

  it("accepts a single callbackPort or a list, within the unprivileged range", () => {
    assert.deepEqual(parseProfile("p", { url: "https://x/mcp", auth: { type: "browser", clientId: "c", callbackPort: 33418 } }).auth.callbackPorts, [33418]);
    assert.deepEqual(parseProfile("p", { url: "https://x/mcp", auth: { type: "browser", clientId: "c", callbackPorts: [1, 2].map((n) => 40000 + n) } }).auth.callbackPorts, [40001, 40002]);
    assert.throws(() => parseProfile("p", { url: "https://x/mcp", auth: { type: "browser", clientId: "c", callbackPorts: [80] } }), /between 1024 and 65535/);
  });

  it("rejects unknown top-level keys and reads every profile", () => {
    assert.throws(() => parseProfiles({ profile: {} }), /Unknown key 'profile'/);
    const profiles = parseProfiles({ profiles: { a: { url: "https://a/mcp", auth: { type: "none" } }, b: { url: "https://b/mcp", auth: { type: "token" } } } });
    assert.deepEqual([...profiles.keys()], ["a", "b"]);
  });

  it("builds a profile from ABP_MCP_* variables (the Claude Desktop extension)", () => {
    const profile = profileFromEnv("desktop", {
      ABP_MCP_URL: "https://api.example.com/mcp", ABP_MCP_AUTH: "password", ABP_MCP_CLIENT_ID: "MyApp_App",
      ABP_MCP_USERNAME: "alice", ABP_MCP_STAGE: "staging", ABP_MCP_SCOPE: "openid offline_access MyApp", ABP_MCP_AUTHORITY: "",
    });
    assert.ok(profile);
    assert.equal(profile.auth.type, "password");
    assert.equal(profile.auth.username, "alice");
    assert.equal(profile.auth.authority, undefined, "empty variables count as unset");
    assert.equal(profile.stage, "staging");
    assert.equal(profileFromEnv("desktop", {}), undefined);
  });

  it("resolves from the file first, then the environment, and names known profiles when missing", () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = path.join(dir, "profiles.json");
      writeProfilesDocument(file, { profiles: { staging: { url: "https://staging/mcp", auth: { type: "none" } } } });
      const env = { ABP_MCP_PROFILES: file, ABP_MCP_URL: "https://env/mcp" };
      assert.equal(resolveProfile("staging", env).url, "https://staging/mcp");
      assert.equal(resolveProfile("other", env).url, "https://env/mcp");
      assert.throws(() => resolveProfile("other", { ABP_MCP_PROFILES: file }), /known: staging/);
    } finally {
      cleanup();
    }
  });

  it("treats a missing file as no profiles and never writes an invalid document", () => {
    const { dir, cleanup } = tempDir();
    try {
      const file = path.join(dir, "profiles.json");
      assert.equal(loadProfiles(file).size, 0);
      assert.throws(() => writeProfilesDocument(file, { profiles: { p: { url: "ftp://x" } } }), ProfileError);
      assert.equal(fs.existsSync(file), false);
    } finally {
      cleanup();
    }
  });
});
