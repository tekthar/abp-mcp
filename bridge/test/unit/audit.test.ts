import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { AUDIT_BACKOFF_MS, AuditLog, type AuditFs } from "../../src/audit.js";
import { Logger } from "../../src/log.js";
import { Redactor } from "../../src/redact.js";
import { tempDir } from "../helpers/harness.js";

function errno(code: string): NodeJS.ErrnoException {
  return Object.assign(new Error(`${code}: operation not permitted`), { code });
}

function capture(): { log: Logger; lines: string[] } {
  const lines: string[] = [];
  return { log: new Logger({ redactor: new Redactor(), sink: (line) => lines.push(line) }), lines };
}

describe("audit log is best-effort (#102)", () => {
  it("keeps appending when another process blocks the rotation (EPERM, EBUSY)", () => {
    const { dir, cleanup } = tempDir();
    try {
      for (const code of ["EPERM", "EBUSY"]) {
        const file = path.join(dir, `${code}.jsonl`);
        fs.writeFileSync(file, "x".repeat(100));
        const blocked: AuditFs = { ...fs, renameSync: () => { throw errno(code); } };
        const { log, lines } = capture();
        const audit = new AuditLog(file, "p", { fs: blocked, maxBytes: 10, log });

        audit.record({ tool: "Book_Get", outcome: "ok", durationMs: 1 });
        audit.record({ tool: "Book_Get", outcome: "ok", durationMs: 2 });
        assert.equal(fs.readFileSync(file, "utf8").split("\n").filter((line) => line.includes("Book_Get")).length, 2);
        assert.equal(lines.filter((line) => line.includes(" WARN ")).length, 0, "a blocked rotation is not worth a warning");
      }
    } finally {
      cleanup();
    }
  });

  it("never throws; warns once, pauses, and tries again after the back-off", () => {
    const clock = { now: 1_000_000 };
    let appends = 0;
    let failing = true;
    const flaky: AuditFs = {
      ...fs,
      mkdirSync: () => undefined,
      statSync: () => { throw errno("ENOENT"); },
      appendFileSync: () => {
        appends++;
        if (failing) {
          throw errno("EACCES");
        }
      },
    };
    const { log, lines } = capture();
    const audit = new AuditLog("/nowhere/p.jsonl", "p", { fs: flaky, log, now: () => clock.now });

    audit.record({ tool: "a", outcome: "ok", durationMs: 1 });
    audit.record({ tool: "b", outcome: "ok", durationMs: 1 });
    assert.equal(appends, 1, "paused after the failure");

    clock.now += AUDIT_BACKOFF_MS;
    audit.record({ tool: "c", outcome: "ok", durationMs: 1 });
    assert.equal(appends, 2, "tried again after the back-off");
    assert.equal(lines.filter((line) => line.includes(" WARN ")).length, 1, "warned once");

    failing = false;
    clock.now += AUDIT_BACKOFF_MS;
    audit.record({ tool: "d", outcome: "ok", durationMs: 1 });
    audit.record({ tool: "e", outcome: "ok", durationMs: 1 });
    assert.equal(appends, 4, "writes resume once the problem is gone");
  });
});
