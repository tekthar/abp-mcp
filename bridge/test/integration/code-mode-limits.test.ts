import assert from "node:assert/strict";
import { after, afterEach, before, describe, it } from "node:test";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { runCode, runCodeTool, type CodeHost } from "../../src/gateway/code-mode.js";
import { TRUNCATED } from "../../src/gateway/truncate.js";
import { Harness } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";

interface RunBody {
  result?: unknown;
  error?: string;
  logs?: string[];
  calls: { tool: string; status: string; targets?: string[]; error?: string }[];
}

const textOf = (result: CallToolResult): string => (result.content[0]?.type === "text" ? result.content[0].text : "");
const body = (result: CallToolResult): RunBody => JSON.parse(textOf(result)) as RunBody;
const statuses = (run: RunBody): string[] => run.calls.map((call) => `${call.tool}:${call.status}`);
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/** A host that answers every call at once and records what reached it. */
function fakeHost(answer: (name: string) => CallToolResult = () => ({ content: [{ type: "text", text: "{}" }] })): CodeHost & { called: string[] } {
  const called: string[] = [];
  return {
    called,
    call: async (name) => {
      called.push(name);
      return answer(name);
    },
    search: async () => ({ results: [] }),
    describe: async () => ({ tools: [] }),
  };
}

describe("run_code limits and lifecycle (0.6.1)", () => {
  let host: McpStub;
  const harnesses: Harness[] = [];
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown): void => {
    unhandled.push(reason);
  };

  before(async () => {
    host = await new McpStub({ discover: true, extraTools: true, gatewayTools: true }).start();
    process.on("unhandledRejection", onUnhandled);
  });

  afterEach(async () => {
    for (const harness of harnesses.splice(0)) {
      await harness.close();
    }
  });

  after(async () => {
    process.off("unhandledRejection", onUnhandled);
    await host.stop();
  });

  async function start(codeLimits: Record<string, number> = {}): Promise<Harness> {
    const harness = new Harness({
      spec: { url: host.url, stage: "development", auth: { type: "none" }, tools: { gateway: true } },
      codeLimits,
    });
    harnesses.push(harness);
    return harness.connect();
  }

  it("sends nothing after Claude cancels the run, even from a catch block that keeps going", async () => {
    const bridge = await start();
    const before = host.ran("Issue_AddComment");
    const cancel = new AbortController();

    const pending = bridge.call("run_code", {
      code: `async () => {
        try { await tools.Book_Slow(); } catch (e) { console.log("slow failed"); }
        for (let i = 0; i < 4; i++) {
          try { await tools.Issue_AddComment({ key: "LIB-1", body: "after-stop-" + i }); } catch (e) { console.log("refused " + i); }
        }
        return "done";
      }`,
    }, cancel.signal);
    await delay(400);
    cancel.abort("user pressed stop");
    await assert.rejects(pending);
    await delay(1_000);

    assert.equal(host.ran("Issue_AddComment"), before, "no call left the bridge after the cancel");
  });

  it("sends nothing after the deadline, and journals the call in flight as unknown", async () => {
    const bridge = await start({ timeoutMs: 500 });
    const before = host.ran("Issue_AddComment");

    const run = body(await bridge.call("run_code", {
      code: `async () => { try { await tools.Book_Slow(); } catch (e) { await tools.Issue_AddComment({ key: "LIB-1", body: "late" }); } }`,
    }));
    await delay(800);

    assert.match(run.error ?? "", /did not finish within 500 ms/);
    assert.deepEqual(statuses(run), ["Book_Slow:unknown"]);
    assert.equal(host.ran("Issue_AddComment"), before, "the catch block's call was never sent");
  });

  it("refuses every call of a run whose request was cancelled before it started", async () => {
    const tools = fakeHost();

    const run = await runCode("async () => { await tools.Issue_AddComment({ key: 'LIB-1' }); return 1; }", tools, undefined, AbortSignal.abort());

    assert.equal(run.error, "The run was cancelled.");
    assert.deepEqual(tools.called, [], "nothing reached the host");
  });

  it("does not end the bridge when a script leaves many host calls pending", async () => {
    const bridge = await start();

    const run = body(await bridge.call("run_code", { code: "async () => { for (let i = 0; i < 30000; i++) __search('issue', 1); return 1; }" }));
    await delay(1_000);

    assert.match(run.error ?? "", /lookup limit \(50 searches and describes\)/);
    assert.deepEqual(unhandled, [], "no unhandled rejection");
    assert.equal(body(await bridge.call("run_code", { code: "async () => 1 + 1" })).result, 2, "the bridge still runs code");
  });

  it("ends deep recursion inside QuickJS, keeps the journal, and keeps working run after run", async () => {
    const bridge = await start();
    const before = host.ran("Issue_AddComment");

    for (let attempt = 0; attempt < 12; attempt++) {
      const result = await bridge.call("run_code", { code: "const f = function (n) { return f(n + 1) + 1; }; return f(0);" });
      assert.equal(result.isError, true);
      assert.match(body(result).error ?? "", /stack/i);
    }

    const parens = await bridge.call("run_code", { code: `return ${"(".repeat(9_000)}1${")".repeat(9_000)};` });
    assert.equal(parens.isError, true, "deeply nested source is a tool error, not a JSON-RPC error");
    assert.match(body(parens).error ?? "", /stack|compile/i);

    const afterWrite = body(await bridge.call("run_code", {
      code: `await tools.Issue_AddComment({ key: "LIB-1", body: "before recursion" }); const f = function (n) { return f(n + 1) + 1; }; return f(0);`,
    }));
    assert.match(afterWrite.error ?? "", /stack/i);
    assert.deepEqual(statuses(afterWrite), ["Issue_AddComment:ok"], "the write is journaled");
    assert.equal(host.ran("Issue_AddComment"), before + 1);

    assert.equal(body(await bridge.call("run_code", { code: "async () => [1, 2, 3].map((x) => x * 2)" })).result?.toString(), "2,4,6");
    assert.deepEqual(unhandled, []);
  });

  it("keeps the journal when the script returns something JSON cannot represent", async () => {
    const bridge = await start();

    const run = body(await bridge.call("run_code", {
      code: `async () => { await tools.Issue_AddComment({ key: "LIB-2", body: "then a function" }); return () => 1; }`,
    }));

    assert.equal(run.result, null);
    assert.deepEqual(statuses(run), ["Issue_AddComment:ok"]);
  });

  it("bounds what a script hands the bridge: logs, errors, search queries and lookups", async () => {
    const tools = fakeHost();

    const logged = await runCodeTool(`async () => { const s = "x".repeat(2 * 1024 * 1024); for (let i = 0; i < 200; i++) __log(s); for (let i = 0; i < 50; i++) console.log("y".repeat(5000)); return "ok"; }`, tools);
    assert.ok(textOf(logged).length < 60_000, `the answer is bounded (${textOf(logged).length} chars)`);
    assert.match(body(logged).logs?.[0] ?? "", new RegExp(`${TRUNCATED} a log line of 2097152 characters`));

    const thrown = await runCodeTool(`async () => { throw new Error("e".repeat(8 * 1024 * 1024)); }`, tools);
    assert.ok(textOf(thrown).length < 30_000, `the error is cut (${textOf(thrown).length} chars)`);

    const query = body(await runCodeTool(`async () => tools.search("zq ".repeat(100000))`, tools));
    assert.match(query.error ?? "", /search query is longer than 1000 characters/);

    const lookups = body(await runCodeTool(`async () => { for (let i = 0; i < 60; i++) await tools.describe(["Issue_Get"]); }`, tools));
    assert.match(lookups.error ?? "", /lookup limit/);
  });

  it("shapes a deep result within the budget, quickly, and answers compact JSON", async () => {
    const tools = fakeHost();
    const started = Date.now();

    const chain = await runCodeTool(`async () => { let v = "x".repeat(65536); for (let i = 0; i < 1000; i++) v = { kkkkkkkkkkkkkkkkkkkkkkkkkkkkkk: v }; return v; }`, tools);
    const nested = await runCodeTool(`async () => { let v = []; for (let i = 1; i < 4000; i++) v = [v]; return v; }`, tools);

    assert.ok(Date.now() - started < 5_000, `shaping is bounded (${Date.now() - started} ms)`);
    assert.ok(textOf(chain).length < 30_000, `${textOf(chain).length}`);
    assert.ok(textOf(nested).length < 30_000, `${textOf(nested).length}`);
    assert.ok(!textOf(nested).includes("\n"), "compact JSON");
  });

  it("journals a host error that can follow a commit as unknown, and names the tools a host batch runs", async () => {
    const tools = fakeHost((name) => name === "mcp_call_tools"
      ? { isError: true, content: [{ type: "text", text: "An error occurred invoking 'mcp_call_tools'." }] }
      : { isError: true, content: [{ type: "text", text: "[INTERNAL] Tool 'x' failed with an internal error; it may have run." }] });

    const run = await runCode(`async () => {
      try { await tools.mcp_call_tools({ calls: [{ name: "Catalog_AddTitle" }, { name: "Member_Get" }] }); } catch (e) {}
      try { await tools.Order_Create({}); } catch (e) {}
    }`, tools);

    assert.deepEqual(run.calls.map((call) => `${call.tool}:${call.status}`), ["mcp_call_tools:unknown", "Order_Create:unknown"]);
    assert.deepEqual(run.calls[0]?.targets, ["Catalog_AddTitle", "Member_Get"]);
  });
});
