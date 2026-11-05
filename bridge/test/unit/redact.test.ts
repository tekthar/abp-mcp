import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { describeError, Logger } from "../../src/log.js";
import { Redactor } from "../../src/redact.js";

describe("redaction", () => {
  it("masks registered secrets wherever they appear", () => {
    const redactor = new Redactor();
    redactor.add("hunter2-password");
    assert.equal(redactor.redact("login with hunter2-password failed"), "login with [REDACTED] failed");
  });

  it("ignores values too short to mask safely", () => {
    const redactor = new Redactor();
    redactor.add("abc");
    assert.equal(redactor.redact("abc"), "abc");
  });

  it("masks secret-bearing shapes even when the value was never registered", () => {
    const redactor = new Redactor();
    assert.equal(redactor.redact("Authorization: Bearer eyJhbGciOi.abc.def"), "Authorization: Bearer [REDACTED]");
    assert.equal(redactor.redact("grant_type=password&username=alice&password=pw1&client_id=x"), "grant_type=password&username=alice&password=[REDACTED]&client_id=x");
    assert.equal(redactor.redact("?code=abc123&state=s"), "?code=[REDACTED]&state=s");
    assert.equal(redactor.redact(`{"refresh_token":"rt-1","expires_in":3600}`), `{"refresh_token":"[REDACTED]","expires_in":3600}`);
  });

  it("is applied to every log line, including error causes", () => {
    const redactor = new Redactor();
    redactor.add("top-secret-token");
    const lines: string[] = [];
    const log = new Logger({ redactor, debug: true, sink: (line) => lines.push(line) });
    log.debug("sending", new Error("outer", { cause: new Error("token top-secret-token rejected") }));
    assert.equal(lines.length, 1);
    assert.doesNotMatch(lines[0]!, /top-secret-token/);
    assert.match(lines[0]!, /\[REDACTED\]/);
  });

  it("drops debug lines unless debugging", () => {
    const lines: string[] = [];
    const log = new Logger({ redactor: new Redactor(), sink: (line) => lines.push(line) });
    log.debug("hidden");
    log.info("shown");
    assert.equal(lines.length, 1);
  });

  it("describes an error with its cause chain", () => {
    assert.equal(describeError(new Error("a", { cause: new Error("b") })), "a <- b");
    assert.equal(describeError("plain"), "plain");
  });
});
