import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { summarize, ToolSearchIndex } from "../../src/gateway/search.js";
import { TRUNCATED, truncateJson } from "../../src/gateway/truncate.js";
import { parseProfile, profileFromEnv } from "../../src/profiles.js";

const tool = (name: string, description: string): Tool => ({ name, description, inputSchema: { type: "object" } });

describe("tool search", () => {
  const index = new ToolSearchIndex([
    tool("jira_create_issue", "Create an issue in a project."),
    tool("jira_get_issue", "Get one issue."),
    tool("jira_add_comment", "Add a comment to an issue."),
    tool("confluence_search", "Search pages; the issue tracker is elsewhere."),
    tool("jira_list_projects", "List projects."),
  ], new Map([["jira_get_issue", "GET"]]));

  it("ranks a name match above a description-only match", () => {
    const names = index.search("issue").map((match) => match.name);
    assert.ok(names.indexOf("jira_get_issue") < names.indexOf("confluence_search"));
    assert.ok(names.includes("confluence_search"), "a description match still counts");
  });

  it("needs every word of a short query", () => {
    assert.deepEqual(index.search("create issue").map((match) => match.name), ["jira_create_issue"]);
    assert.deepEqual(index.search("create page"), []);
  });

  it("needs most words of a long query", () => {
    assert.equal(index.search("please add a comment to the issue")[0]?.name, "jira_add_comment");
  });

  it("matches camelCase and plurals, and carries the HTTP method", () => {
    const camel = new ToolSearchIndex([tool("getIssueById", "x")]);
    assert.equal(camel.search("issue by id")[0]?.name, "getIssueById");
    assert.equal(index.search("project")[0]?.name, "jira_list_projects");
    assert.equal(index.search("get issue")[0]?.httpMethod, "GET");
  });

  it("caps the number of results", () => {
    assert.equal(index.search("jira", 2).length, 2);
    assert.deepEqual(index.search("   "), []);
  });

  it("summarizes to the first sentence, cut to a line", () => {
    assert.equal(summarize("First sentence. Second one."), "First sentence.");
    assert.ok(summarize("x".repeat(500)).length <= 160);
  });
});

describe("result truncation", () => {
  it("leaves a small value alone", () => {
    const value = { a: [1, 2], b: "c" };
    assert.equal(truncateJson(value, 1000), value);
  });

  it("cuts the largest parts first and stays valid JSON within the budget", () => {
    const value = { summary: "short", rows: Array.from({ length: 500 }, (_, i) => ({ id: i, text: "y".repeat(100) })), note: "z".repeat(5000) };
    const cut = truncateJson(value, 4000) as { summary: string; rows: unknown[]; note: string };
    const json = JSON.stringify(cut);
    assert.ok(json.length <= 4000, `${json.length}`);
    assert.equal(cut.summary, "short", "small values survive");
    assert.ok(cut.rows.length < 500);
    assert.match(String(cut.rows.at(-1)), new RegExp(`${TRUNCATED} \\d+ more item`));
    assert.ok(cut.note.endsWith(TRUNCATED));
  });
});

describe("bounded work (0.6.1)", () => {
  it("cuts a value too deep to shape as text that fits the budget, without throwing", () => {
    let chain: unknown = "x".repeat(65_536);
    for (let level = 0; level < 2_500; level++) {
      chain = { kkkkkkkkkkkkkkkkkkkkkkkkkkkkkk: chain };
    }

    const started = Date.now();
    const cut = truncateJson(chain, 24_000);
    assert.ok(Date.now() - started < 1_000, "linear in the value, not in depth x size");
    assert.equal(typeof cut, "string");
    assert.ok(JSON.stringify(cut).length <= 24_000, `${JSON.stringify(cut).length}`);
    assert.ok((cut as string).endsWith(TRUNCATED));
  });

  it("the text fallback fits the budget once encoded, even when the JSON escapes many characters", () => {
    const quotes = Array.from({ length: 50 }, () => '"'.repeat(1_000));
    const cut = truncateJson(Object.fromEntries(Array.from({ length: 40 }, (_, index) => [`k${index}`, { deep: { deeper: { deepest: quotes } } }])), 4_000);
    assert.ok(JSON.stringify(cut).length <= 4_000, `${JSON.stringify(cut).length}`);
  });

  it("scores at most a few dozen distinct words of a huge query", () => {
    const many = new ToolSearchIndex(Array.from({ length: 200 }, (_, index) => tool(`Widget_Op${index}`, "Operate on a widget.")));
    const started = Date.now();
    many.search("zq ".repeat(3_000_000));
    assert.ok(Date.now() - started < 500, `${Date.now() - started} ms`);
    assert.equal(many.search("widget", Number.NaN).length, 10, "a limit that is not a number means the default");
  });
});

describe("gateway profile settings", () => {
  it("reads tools.gateway as true or an object", () => {
    assert.deepEqual(parseProfile("a", { url: "https://h/mcp", tools: { gateway: true } }).tools.gateway, { pinned: [], code: true });
    assert.deepEqual(parseProfile("a", { url: "https://h/mcp", tools: { gateway: { pinned: ["x*"], code: false } } }).tools.gateway, { pinned: ["x*"], code: false });
    assert.equal(parseProfile("a", { url: "https://h/mcp" }).tools.gateway, undefined);
    assert.throws(() => parseProfile("a", { url: "https://h/mcp", tools: { gateway: { pinned: ["x"], typo: 1 } } }));
  });

  it("reads ABP_MCP_GATEWAY, ABP_MCP_GATEWAY_PINNED and ABP_MCP_GATEWAY_CODE", () => {
    const profile = profileFromEnv("ext", {
      ABP_MCP_URL: "https://h/mcp",
      ABP_MCP_GATEWAY: "on",
      ABP_MCP_GATEWAY_PINNED: "jira_get_issue, jira_search*",
      ABP_MCP_GATEWAY_CODE: "off",
    });
    assert.deepEqual(profile?.tools.gateway, { pinned: ["jira_get_issue", "jira_search*"], code: false });
    assert.equal(profileFromEnv("ext", { ABP_MCP_URL: "https://h/mcp" })?.tools.gateway, undefined);
  });
});
