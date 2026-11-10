import fs from "node:fs";
import assert from "node:assert/strict";
import { after, afterEach, before, describe, it } from "node:test";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { Harness } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";

const text = (result: CallToolResult): string => (result.content[0]?.type === "text" ? result.content[0].text : "");
const parsed = <T>(result: CallToolResult): T => JSON.parse(text(result)) as T;

describe("gateway mode (#120)", () => {
  let host: McpStub;
  const harnesses: Harness[] = [];

  before(async () => {
    host = await new McpStub({ discover: true, extraTools: true }).start();
  });

  afterEach(async () => {
    for (const harness of harnesses.splice(0)) {
      await harness.close();
    }
  });

  after(async () => {
    await host.stop();
  });

  async function start(tools: Record<string, unknown>, stage = "development"): Promise<Harness> {
    const harness = new Harness({ spec: { url: host.url, stage, auth: { type: "none" }, tools } });
    harnesses.push(harness);
    return harness.connect();
  }

  it("lists only the pinned tools and the gateway's own", async () => {
    const bridge = await start({ gateway: { pinned: ["Issue_Get", "Project_*"] } });

    assert.deepEqual(await bridge.listToolNames(), ["Issue_Get", "Project_List", "call_tool", "describe_tools", "run_code", "search_tools"]);
    const search = (await bridge.client.listTools()).tools.find((tool) => tool.name === "search_tools");
    assert.match(search?.description ?? "", /not listed here \(11 more\)/, "the description counts the unlisted tools");
    assert.match(bridge.client.getInstructions() ?? "", /Find the others with search_tools/);
  });

  it("lists no run_code when code is off, and nothing extra when the gateway is off", async () => {
    const noCode = await start({ gateway: { pinned: [], code: false } });
    assert.deepEqual(await noCode.listToolNames(), ["call_tool", "describe_tools", "search_tools"]);

    const plain = await start({});
    assert.ok((await plain.listToolNames()).includes("Issue_Search"));
    assert.ok(!(await plain.listToolNames()).includes("search_tools"));
  });

  it("search ranks a name match first and covers only what the profile allows", async () => {
    const bridge = await start({ gateway: true, deny: ["Issue_Export"] });

    const found = parsed<{ results: { name: string; description: string; httpMethod?: string }[]; total: number }>(
      await bridge.call("search_tools", { query: "issue comment" }));
    assert.equal(found.results[0]?.name, "Issue_AddComment");
    assert.equal(found.results[0]?.description, "Add a comment to an issue.");

    const export_ = parsed<{ results: { name: string }[] }>(await bridge.call("search_tools", { query: "export" }));
    assert.deepEqual(export_.results, [], "a denied tool is never found");

    const projects = parsed<{ results: { name: string }[] }>(await bridge.call("search_tools", { query: "projects" }));
    assert.equal(projects.results[0]?.name, "Project_List", "a word matches its plural (project/projects)");
  });

  it("describe returns schemas and reports unknown names without revealing hidden tools", async () => {
    const bridge = await start({ gateway: true, deny: ["Issue_Export"] });

    const described = parsed<{ tools: { name: string; inputSchema: { properties: Record<string, unknown> }; outputSchema?: unknown }[]; notFound?: string[] }>(
      await bridge.call("describe_tools", { names: ["Issue_Get", "Issue_Export", "Nope"] }));
    assert.deepEqual(described.tools.map((tool) => tool.name), ["Issue_Get"]);
    assert.ok("key" in described.tools[0]!.inputSchema.properties);
    assert.ok(described.tools[0]!.outputSchema, "the output schema comes along");
    assert.deepEqual(described.notFound, ["Issue_Export", "Nope"]);
  });

  it("call_tool runs an unlisted tool once and returns its result unchanged", async () => {
    const bridge = await start({ gateway: true });
    const before = host.ran("Issue_Get");

    const result = await bridge.call("call_tool", { name: "Issue_Get", arguments: { key: "LIB-2" } });

    assert.notEqual(result.isError, true, text(result));
    assert.deepEqual(result.structuredContent, { key: "LIB-2", title: "Add e-book loans", status: "Open" });
    assert.equal(host.ran("Issue_Get"), before + 1);
  });

  it("call_tool follows the read-only preset: a write tool is refused locally and never sent", async () => {
    const bridge = await start({ gateway: true }, "production");
    const before = host.ran("Issue_AddComment");

    const result = await bridge.call("call_tool", { name: "Issue_AddComment", arguments: { key: "LIB-1", body: "x" } });

    assert.equal(result.isError, true);
    assert.match(text(result), /^\[BRIDGE_TOOL_HIDDEN\]/);
    assert.equal(host.ran("Issue_AddComment"), before);
    const found = parsed<{ results: { name: string }[] }>(await bridge.call("search_tools", { query: "comment" }));
    assert.deepEqual(found.results, [], "search covers only the read-only tools in production");
  });

  it("rejects bad arguments with a clear error", async () => {
    const bridge = await start({ gateway: true });
    assert.match(text(await bridge.call("search_tools", {})), /^\[BRIDGE_INVALID_ARGUMENTS\] 'query' must be a non-empty string/);
    assert.match(text(await bridge.call("describe_tools", { names: [] })), /^\[BRIDGE_INVALID_ARGUMENTS\]/);
    assert.match(text(await bridge.call("call_tool", { name: "Issue_Get", arguments: [1] })), /'arguments' must be an object/);
  });

  it("searches the new list after the host's tools/list_changed", async () => {
    const bridge = await start({ gateway: true });
    await bridge.listToolNames();
    const cached = (bridge.bridge as unknown as { allowedTools?: unknown[] }).allowedTools;
    assert.ok(cached && cached.length > 0, "the list is cached after tools/list");

    bridge.bridge.onUpstreamToolsChanged();

    assert.equal((bridge.bridge as unknown as { allowedTools?: unknown[] }).allowedTools, undefined, "the cache is dropped");
    const found = parsed<{ results: { name: string }[] }>(await bridge.call("search_tools", { query: "issue get" }));
    assert.equal(found.results[0]?.name, "Issue_Get", "search reads the list again");
  });
});

describe("the host's call-by-name gateway tools through the bridge's filter (0.6.1)", () => {
  let host: McpStub;
  const harnesses: Harness[] = [];

  before(async () => {
    host = await new McpStub({ discover: true, extraTools: true, gatewayTools: true }).start();
  });

  afterEach(async () => {
    for (const harness of harnesses.splice(0)) {
      await harness.close();
    }
  });

  after(async () => {
    await host.stop();
  });

  async function start(tools: Record<string, unknown>, stage = "staging"): Promise<Harness> {
    const harness = new Harness({ spec: { url: host.url, stage, auth: { type: "none" }, tools }, audit: true });
    harnesses.push(harness);
    return harness.connect();
  }

  it("refuses a denied tool wrapped in mcp_call_tool or mcp_call_tools, on every path, and never sends it", async () => {
    const bridge = await start({ deny: ["*_Delete"], gateway: true });
    const before = host.ran("Book_Delete");
    const beforeGet = host.ran("Book_Get");

    const attempts = [
      await bridge.call("mcp_call_tool", { name: "Book_Delete" }),
      await bridge.call("call_tool", { name: "mcp_call_tool", arguments: { name: "Book_Delete" } }),
      await bridge.call("mcp_call_tools", { calls: [{ name: "Book_Get" }, { name: "Book_Delete" }] }),
    ];
    const run = parsed<{ error?: string; calls: { tool: string; status: string }[] }>(await bridge.call("run_code", {
      code: "async () => tools.mcp_call_tools({ calls: [{ name: 'Book_Delete' }] })",
    }));

    for (const result of attempts) {
      assert.equal(result.isError, true);
      assert.match(text(result), /^\[BRIDGE_TOOL_HIDDEN\] Tool "Book_Delete", called through 'mcp_call_tools?',.*tools\.deny pattern '\*_Delete'.*not sent to the host/);
    }

    assert.match(run.error ?? "", /\[BRIDGE_TOOL_HIDDEN\] Tool "Book_Delete", called through 'mcp_call_tools'/);
    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["mcp_call_tools:refused"]);
    assert.equal(host.ran("Book_Delete"), before, "the denied tool never ran");
    assert.equal(host.ran("Book_Get"), beforeGet, "a batch with one hidden tool is refused as a whole");
  });

  it("refuses padded or missing inner names, which a host could resolve to a different tool than the filter saw", async () => {
    const bridge = await start({ deny: ["*_Delete"], gateway: true });

    for (const args of [{ name: "Book_Delete " }, { name: "Book_Delete\u0085" }, {}, { name: 42 }]) {
      const result = await bridge.call("mcp_call_tool", args);
      assert.match(text(result), /^\[BRIDGE_TOOL_HIDDEN\].*missing, or contains spaces or control characters/, JSON.stringify(args));
    }

    assert.match(text(await bridge.call("mcp_call_tools", { calls: [] })), /^\[BRIDGE_TOOL_HIDDEN\]/);
  });

  it("runs allowed inner tools and names them in the audit log", async () => {
    const bridge = await start({ deny: ["*_Delete"], gateway: true });

    const result = await bridge.call("mcp_call_tools", { calls: [{ name: "Book_Get" }, { name: "Issue_Search" }] });

    assert.notEqual(result.isError, true, text(result));
    const entries = fs.readFileSync(bridge.auditFile, "utf8").trim().split("\n").map((line) => JSON.parse(line) as { tool: string; targets?: string[]; outcome: string });
    assert.deepEqual(entries.at(-1), { ...entries.at(-1), tool: "mcp_call_tools", targets: ["Book_Get", "Issue_Search"], outcome: "ok" });
  });

  it("applies the read-only preset to inner tools even when include lets the wrapper through", async () => {
    const bridge = await start({ include: ["mcp_*"], gateway: true }, "production");
    const before = host.ran("Book_Delete");

    const refused = await bridge.call("mcp_call_tool", { name: "Book_Delete" });
    const allowed = await bridge.call("mcp_call_tool", { name: "Book_Get" });

    assert.match(text(refused), /^\[BRIDGE_TOOL_HIDDEN\] Tool "Book_Delete", called through 'mcp_call_tool',.*GET endpoints/);
    assert.equal(text(allowed), "ran Book_Get");
    assert.equal(host.ran("Book_Delete"), before);
  });
});

