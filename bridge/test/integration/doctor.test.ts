import assert from "node:assert/strict";
import { after, afterEach, before, describe, it } from "node:test";
import { doctor } from "../../src/commands/doctor.js";
import type { Io } from "../../src/io.js";
import { createRuntime } from "../../src/runtime.js";
import { tempDir } from "../helpers/harness.js";
import { McpStub, type McpStubOptions } from "../helpers/mcp-stub.js";
import { OAuthStub } from "../helpers/oauth-stub.js";

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

describe("doctor", () => {
  const oauth = new OAuthStub();
  const temp = tempDir();
  let host: McpStub | undefined;
  let counter = 0;

  before(async () => {
    await oauth.start();
  });

  afterEach(async () => {
    await host?.stop();
    host = undefined;
  });

  after(async () => {
    await oauth.stop();
    temp.cleanup();
  });

  async function run(stub: Omit<McpStubOptions, "isValidToken" | "issuer">, auth: Record<string, string>, configure?: (host: McpStub) => void) {
    host = await new McpStub({ isValidToken: (token) => oauth.isValid(token), issuer: oauth.issuer, ...stub }).start();
    configure?.(host);
    const env: NodeJS.ProcessEnv = {
      ABP_MCP_CONFIG_DIR: `${temp.dir}/c${++counter}`,
      ABP_MCP_PROFILES: `${temp.dir}/none.json`,
      ABP_MCP_KEYCHAIN: "off",
      ABP_MCP_URL: host.url,
      ABP_MCP_TOOLS_PRESET: "all",
      ...auth,
    };
    const runtime = await createRuntime("doc", { env, logSink: () => undefined });
    const io = recordingIo();
    return { runtime, io, run: async () => await doctor(runtime, { tools: false }, io) };
  }

  const password = () => ({
    ABP_MCP_AUTH: "password", ABP_MCP_CLIENT_ID: "MyApp_App", ABP_MCP_AUTHORITY: oauth.issuer,
    ABP_MCP_SCOPE: "openid offline_access Library", ABP_MCP_USERNAME: "alice", ABP_MCP_PASSWORD: "correct horse",
  });

  it("reports an open sign-in pause with the time left, and stops there (#107)", async () => {
    const { runtime, io, run: go } = await run({}, password());
    runtime.breaker.trip("invalid_grant");

    assert.equal(await go(), false);
    const output = io.lines.join("\n");
    assert.match(output, /✗ Sign-in pause: paused for another (1[45] min \d+ s), until \S+Z \(invalid_grant\)/);
    assert.match(output, /Fix: Fix the credentials, then run `abp-mcp login doc`\./);
    assert.doesNotMatch(output, /tools\/list/, "no step after the pause runs");
  });

  it("names the tools refused for undefined permissions, from /mcp/_explain (#108)", async () => {
    const { io, run: go } = await run({ undefinedPermissions: [{ tool: "Book_Admin", permission: "Library.Admin" }] }, password());

    assert.equal(await go(), true, "an undefined permission is flagged, not a failure");
    const output = io.lines.join("\n");
    assert.match(output, /! \/mcp\/_explain: 1 tool\(s\) refused for every caller.*Book_Admin \(Library\.Admin\)/);
    assert.match(output, /Fix: Define the permission/);
  });

  it("says when _explain reports no undefined permission, or is unavailable", async () => {
    const clean = await run({ undefinedPermissions: [] }, password());
    assert.equal(await clean.run(), true);
    assert.match(clean.io.lines.join("\n"), /✓ \/mcp\/_explain: no tool requires an undefined permission/);
    await host?.stop();

    const missing = await run({}, password());
    assert.equal(await missing.run(), true);
    assert.match(missing.io.lines.join("\n"), /✓ \/mcp\/_explain: unavailable/);
  });

  it("shows whether the host provides instructions (#104)", async () => {
    const { io, run: go } = await run({}, password());
    assert.equal(await go(), true);
    assert.match(io.lines.join("\n"), /✓ Host instructions: 18 characters: "Stub library host\."/);
  });

  it("hints at AbpMcp 0.4.1 when tools/list fails with -32603 (#108)", async () => {
    const { io, run: go } = await run({}, password(), (stub) => {
      stub.failToolsList = true;
    });
    assert.equal(await go(), false);
    assert.match(io.lines.join("\n"), /✗ tools\/list: .*-32603[\s\S]*Fix: .*AbpMcp 0\.4\.1 or later/);
  });

  describe("without auth.authority (#108)", () => {
    const browser = () => ({ ABP_MCP_AUTH: "browser", ABP_MCP_CLIENT_ID: "MyApp_Mcp" });

    it("passes when the 401 challenge names resource_metadata", async () => {
      const { io, run: go } = await run({}, browser());
      await go();
      assert.match(io.lines.join("\n"), /✓ Protected-resource metadata: the 401 challenge names http:\/\/127\.0\.0\.1:\d+\/\.well-known\/oauth-protected-resource\/mcp/);
    });

    it("passes when only the RFC 9728 well-known location has it", async () => {
      const { io, run: go } = await run({ resourceMetadata: "well-known" }, browser());
      await go();
      assert.match(io.lines.join("\n"), /✓ Protected-resource metadata: the challenge names none, but \S+\/\.well-known\/oauth-protected-resource\/mcp exists/);
    });

    it("fails when neither the challenge nor the well-known locations name it, and points at the guide", async () => {
      const { io, run: go } = await run({ resourceMetadata: "none" }, browser());
      assert.equal(await go(), false);
      const output = io.lines.join("\n");
      assert.match(output, /✗ Protected-resource metadata: the 401 challenge has no resource_metadata, and no RFC 9728 document/);
      assert.match(output, /Fix: Set auth\.authority .*docs\/oauth\.md/);
      assert.doesNotMatch(output, /Authorization server/, "doctor stops at the first failing step");
    });

    it("is skipped when the profile sets auth.authority", async () => {
      const { io, run: go } = await run({ resourceMetadata: "none" }, password());
      await go();
      assert.doesNotMatch(io.lines.join("\n"), /Protected-resource metadata/);
    });
  });
});
