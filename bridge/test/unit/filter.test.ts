import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { globToRegExp, ToolFilter } from "../../src/filter.js";
import type { ToolsConfig } from "../../src/profiles.js";

const config = (overrides: Partial<ToolsConfig>): ToolsConfig => ({ preset: "all", allow: [], deny: [], include: [], ...overrides });

describe("tool filter", () => {
  it("matches globs case-insensitively and anchored", () => {
    assert.ok(globToRegExp("*_Delete").test("Book_delete"));
    assert.ok(!globToRegExp("*_Delete").test("Book_DeleteMany"));
    assert.ok(globToRegExp("Book_?et").test("Book_Get"));
    assert.ok(!globToRegExp("Book.Get").test("BookXGet"), "dots are literal");
  });

  it("shows everything with the all preset and no globs", () => {
    assert.equal(new ToolFilter(config({})).decide({ name: "Book_Delete" }).visible, true);
  });

  it("hides denied tools, and deny wins over allow and include", () => {
    const filter = new ToolFilter(config({ preset: "read-only", allow: ["Book_*"], include: ["Book_Delete"], deny: ["*_Delete"] }));
    const decision = filter.decide({ name: "Book_Delete", httpMethod: "DELETE" });
    assert.equal(decision.visible, false);
    assert.match((decision as { reason: string }).reason, /tools\.deny pattern '\*_Delete'/);
  });

  it("with allow globs, hides every tool that matches none", () => {
    const filter = new ToolFilter(config({ allow: ["Book_Get", "Member_*"] }));
    assert.equal(filter.decide({ name: "Member_List" }).visible, true);
    assert.equal(filter.decide({ name: "Book_Delete" }).visible, false);
  });

  it("read-only shows GET tools only, and fails closed without HTTP methods", () => {
    const filter = new ToolFilter(config({ preset: "read-only" }));
    assert.equal(filter.needsHttpMethods, true);
    assert.equal(filter.decide({ name: "Book_Get", httpMethod: "get" }).visible, true);
    assert.equal(filter.decide({ name: "Book_Delete", httpMethod: "DELETE" }).visible, false);
    assert.equal(filter.decide({ name: "Library_Overview", httpMethod: null }).visible, false, "a dynamic tool has no HTTP method");
    const unknown = filter.decide({ name: "Book_Get" });
    assert.equal(unknown.visible, false, "no _discover means no proof a tool only reads");
    assert.match((unknown as { reason: string }).reason, /_discover/);
  });

  it("include shows tools the preset would hide", () => {
    const filter = new ToolFilter(config({ preset: "read-only", include: ["*_List"] }));
    assert.equal(filter.decide({ name: "Book_List", httpMethod: "POST" }).visible, true);
    assert.equal(filter.decide({ name: "Book_Delete", httpMethod: "DELETE" }).visible, false);
  });

  it("does not need HTTP methods unless the preset is read-only", () => {
    assert.equal(new ToolFilter(config({ deny: ["x"] })).needsHttpMethods, false);
  });
});
