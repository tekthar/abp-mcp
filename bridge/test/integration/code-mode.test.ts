import assert from "node:assert/strict";
import { after, afterEach, before, describe, it } from "node:test";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { TRUNCATED } from "../../src/gateway/truncate.js";
import { Harness } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";

interface RunBody {
  result?: unknown;
  error?: string;
  logs?: string[];
  calls: { tool: string; status: string; error?: string }[];
}

const body = (result: CallToolResult): RunBody =>
  JSON.parse(result.content[0]?.type === "text" ? result.content[0].text : "{}") as RunBody;

describe("code mode: run_code (#121)", () => {
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

  async function start(options: { stage?: string; codeLimits?: Record<string, number> } = {}): Promise<Harness> {
    const harness = new Harness({
      spec: { url: host.url, stage: options.stage ?? "development", auth: { type: "none" }, tools: { gateway: true } },
      ...(options.codeLimits ? { codeLimits: options.codeLimits } : {}),
    });

    harnesses.push(harness);
    return harness.connect();
  }

  it("chains several tools and returns only the combined value; each ran once", async () => {
    const bridge = await start();
    const searches = host.ran("Issue_Search");
    const comments = host.ran("Issue_AddComment");

    const result = await bridge.call("run_code", {
      code: `async () => {
        const { issues } = await tools.Issue_Search({ type: "Bug", status: "Open" });
        await Promise.all(issues.map((i) => tools.Issue_AddComment({ key: i.key, body: "Triaged" })));
        console.log("commented on", issues.length);
        return issues.map((i) => i.key);
      }`,
    });

    const run = body(result);
    assert.notEqual(result.isError, true, JSON.stringify(run));
    assert.deepEqual(run.result, ["LIB-1", "LIB-3"]);
    assert.deepEqual(run.logs, ["commented on 2"]);
    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["Issue_Search:ok", "Issue_AddComment:ok", "Issue_AddComment:ok"]);
    assert.equal(host.ran("Issue_Search"), searches + 1);
    assert.equal(host.ran("Issue_AddComment"), comments + 2);
  });

  it("gives the script structured results, and search and describe", async () => {
    const bridge = await start();

    const run = body(await bridge.call("run_code", {
      code: `async () => {
        const found = await tools.search("get issue");
        const described = await tools.describe([found.results[0].name]);
        const issue = await tools.call(found.results[0].name, { key: "LIB-1" });
        return { name: found.results[0].name, hasKey: "key" in described.tools[0].inputSchema.properties, title: issue.title };
      }`,
    }));

    assert.deepEqual(run.result, { name: "Issue_Get", hasKey: true, title: "Catalog search is slow" });
  });

  it("a tool error throws in the script, and the journal shows the calls that already ran", async () => {
    const bridge = await start();

    const result = await bridge.call("run_code", {
      code: `async () => {
        await tools.Issue_AddComment({ key: "LIB-2", body: "Starting" });
        await tools.Issue_Transition({ key: "LIB-2", to: "Done" });
        await tools.Issue_AddComment({ key: "LIB-2", body: "Done" });
      }`,
    });

    const run = body(result);
    assert.equal(result.isError, true);
    assert.match(run.error ?? "", /\[BUSINESS_ERROR\] LIB-2 cannot move to Done/);
    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["Issue_AddComment:ok", "Issue_Transition:error"]);
  });

  it("a script can catch a tool error and carry on", async () => {
    const bridge = await start();
    const run = body(await bridge.call("run_code", {
      code: `async () => { try { await tools.Issue_Get({ key: "NOPE-1" }); } catch (e) { return "caught: " + e.message; } }`,
    }));
    assert.equal(run.result, "caught: [NOT_FOUND] No issue NOPE-1.");
  });

  it("stops a loop that never ends at the deadline", async () => {
    const bridge = await start({ codeLimits: { timeoutMs: 300 } });
    const started = Date.now();

    const run = body(await bridge.call("run_code", { code: "async () => { while (true) {} }" }));

    assert.match(run.error ?? "", /did not finish within 300 ms/);
    assert.ok(Date.now() - started < 5_000);
  });

  it("journals a call still in flight at the deadline as unknown: it was sent and may have run", async () => {
    const bridge = await start({ codeLimits: { timeoutMs: 500 } });
    const before = host.ran("Book_Slow");

    const run = body(await bridge.call("run_code", { code: "async () => { await tools.Book_Slow(); return 'never'; }" }));

    assert.match(run.error ?? "", /did not finish within 500 ms/);
    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["Book_Slow:unknown"]);
    assert.equal(host.ran("Book_Slow"), before + 1, "the call reached the host");
    assert.deepEqual(body(await bridge.call("run_code", { code: "async () => 1 + 1" })).result, 2, "the bridge still runs code afterwards");
  });

  it("refuses calls over the limit without sending them", async () => {
    const bridge = await start({ codeLimits: { maxCalls: 2 } });
    const before = host.ran("Project_List");

    const run = body(await bridge.call("run_code", {
      code: "async () => { for (let i = 0; i < 3; i++) { await tools.Project_List(); } }",
    }));

    assert.match(run.error ?? "", /tool call limit \(2\) was reached/);
    assert.equal(run.calls.length, 2);
    assert.equal(host.ran("Project_List"), before + 2);
  });

  it("has no Node, network or file access", async () => {
    const bridge = await start();
    const run = body(await bridge.call("run_code", {
      code: "async () => [typeof process, typeof require, typeof fetch, typeof XMLHttpRequest, typeof globalThis.os, typeof globalThis.std]",
    }));
    assert.deepEqual(run.result, ["undefined", "undefined", "undefined", "undefined", "undefined", "undefined"]);
  });

  it("follows the read-only preset: a write tool is refused from code too", async () => {
    const bridge = await start({ stage: "production" });
    const before = host.ran("Issue_AddComment");

    const run = body(await bridge.call("run_code", {
      code: `async () => tools.Issue_AddComment({ key: "LIB-1", body: "x" })`,
    }));

    assert.match(run.error ?? "", /\[BRIDGE_TOOL_HIDDEN\]/);
    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["Issue_AddComment:refused"]);
    assert.equal(host.ran("Issue_AddComment"), before);
  });

  it("truncates a large result and keeps it valid JSON", async () => {
    const bridge = await start();
    const result = await bridge.call("run_code", { code: "async () => tools.Issue_Export()" });

    const raw = result.content[0]?.type === "text" ? result.content[0].text : "";
    const run = JSON.parse(raw) as RunBody & { result: { rows: unknown[] } };
    assert.ok(raw.length < 30_000, `the answer is cut (${raw.length} chars)`);
    assert.ok(Array.isArray(run.result.rows));
    assert.match(JSON.stringify(run.result), new RegExp(TRUNCATED));
  });

  it("reports code that does not compile", async () => {
    const bridge = await start();
    const result = await bridge.call("run_code", { code: "async () => { return ; ; ) }" });
    assert.equal(result.isError, true);
    assert.match(body(result).error ?? "", /does not compile/);
  });

  it("accepts a bare function body", async () => {
    const bridge = await start();
    const run = body(await bridge.call("run_code", { code: "const p = await tools.Project_List(); return p[0].key;" }));
    assert.equal(run.result, "LIB");
  });
});
