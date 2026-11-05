import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { after, before, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { CallToolResultSchema, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { tempDir } from "../helpers/harness.js";

/**
 * Drives the built bridge over stdio, exactly as Claude starts it, against the AbpMcp Library sample
 * (`dotnet run --project samples/AbpMcp.Sample`). Set ABP_MCP_E2E_URL to the sample's /mcp URL to run.
 */
const sampleUrl = process.env.ABP_MCP_E2E_URL;
const bin = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../dist/bin.js");

describe("end to end against the Library sample", { skip: sampleUrl ? false : "ABP_MCP_E2E_URL is not set" }, () => {
  const temp = tempDir();
  const profiles = path.join(temp.dir, "profiles.json");
  const env = {
    ...process.env,
    ABP_MCP_PROFILES: profiles,
    ABP_MCP_CONFIG_DIR: temp.dir,
    ABP_MCP_KEYCHAIN: "off",
    ABP_MCP_AUDIT: "1",
  } as Record<string, string>;

  before(() => {
    fs.writeFileSync(profiles, JSON.stringify({
      profiles: {
        dev: { url: sampleUrl, auth: { type: "none" }, notes: "Library sample." },
        prod: { url: sampleUrl, stage: "production", auth: { type: "none" } },
        "prod-lists": { url: sampleUrl, stage: "production", auth: { type: "none" }, tools: { include: ["*_List"] } },
      },
    }));
  });

  after(() => temp.cleanup());

  async function connect(profile: string): Promise<Client> {
    const client = new Client({ name: "e2e-claude", version: "1.0.0" });
    await client.connect(new StdioClientTransport({ command: process.execPath, args: [bin, "--profile", profile], env, stderr: "ignore" }));
    return client;
  }

  async function call(client: Client, name: string, args: Record<string, unknown> = {}): Promise<CallToolResult> {
    return await client.request({ method: "tools/call", params: { name, arguments: args } }, CallToolResultSchema) as CallToolResult;
  }

  const text = (result: CallToolResult): string => (result.content[0]?.type === "text" ? result.content[0].text : "");

  it("forwards initialize, tools/list and tools/call", async () => {
    const client = await connect("dev");
    try {
      assert.equal(client.getServerVersion()?.name, "AbpMcp.Sample", "the host's serverInfo is forwarded");
      assert.match(client.getInstructions() ?? "", /profile 'dev' \(development\)/);
      assert.match(client.getInstructions() ?? "", /Library sample\./);

      const names = (await client.listTools()).tools.map((tool) => tool.name);
      for (const expected of ["Member_List", "Loan_CheckOut", "Catalog_GetTitle", "Library_Overview"]) {
        assert.ok(names.includes(expected), `${expected} is listed`);
      }

      const overview = JSON.parse(text(await call(client, "Library_Overview"))) as { titles: number };
      assert.ok(overview.titles > 0);
    } finally {
      await client.close();
    }
  });

  it("passes the host's error codes through", async () => {
    const client = await connect("dev");
    try {
      const result = await call(client, "Loan_CheckOut");
      assert.equal(result.isError, true);
      assert.match(text(result), /^\[VALIDATION_ERROR\]/);
    } finally {
      await client.close();
    }

    const audit = fs.readFileSync(path.join(temp.dir, "state", "audit", "dev.jsonl"), "utf8");
    assert.match(audit, /"tool":"Loan_CheckOut","outcome":"tool_error"/);
  });

  it("a production profile lists only GET tools and refuses writes locally", async () => {
    const client = await connect("prod");
    try {
      assert.match(client.getInstructions() ?? "", /PRODUCTION/);
      const names = (await client.listTools()).tools.map((tool) => tool.name).sort();
      assert.ok(names.length > 0);
      assert.ok(names.includes("Catalog_GetTitle"));
      assert.ok(!names.includes("Loan_CheckOut"));

      const refused = await call(client, "Loan_CheckOut");
      assert.match(text(refused), /^\[BRIDGE_TOOL_HIDDEN\]/);
    } finally {
      await client.close();
    }
  });

  it("tools.include shows a read-only POST tool under the read-only preset", async () => {
    const client = await connect("prod-lists");
    try {
      const names = (await client.listTools()).tools.map((tool) => tool.name);
      assert.ok(names.includes("Member_List"));
      assert.equal((await call(client, "Member_List")).isError ?? false, false);
    } finally {
      await client.close();
    }
  });

  it("doctor checks the profile up to tools/list", async () => {
    const { stdout } = await promisify(execFile)(process.execPath, [bin, "doctor", "dev", "--tools"], { env });
    assert.match(stdout, /✓ tools\/list: \d+ tool\(s\) from AbpMcp\.Sample/);
    assert.match(stdout, /Tools: \d+ of \d+ shown/);
  });
});
