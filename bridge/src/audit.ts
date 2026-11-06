import fs from "node:fs";
import path from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Logger } from "./log.js";

export type AuditOutcome = "ok" | "tool_error" | "refused" | "failed" | "cancelled";

export interface AuditEntry {
  readonly tool: string;
  readonly outcome: AuditOutcome;
  readonly durationMs: number;
  readonly errorCode?: string;
}

const MAX_BYTES = 5 * 1024 * 1024;
const KEEP = 3;

/** After a failed write, the log skips writing for this long before it tries again. */
export const AUDIT_BACKOFF_MS = 60_000;

// A rename that another process (a second bridge on the same profile, antivirus) holds open, or already did.
const ROTATION_BUSY_CODES = new Set(["EPERM", "EBUSY", "EACCES", "ENOENT"]);

/** The file operations the audit log uses; replaceable in tests. */
export type AuditFs = Pick<typeof fs, "mkdirSync" | "statSync" | "existsSync" | "renameSync" | "appendFileSync">;

export interface AuditLogOptions {
  /** Where the one warning about a failed write goes. Its lines pass through the redactor. */
  readonly log?: Logger;
  /** Rotate when the file reaches this size. */
  readonly maxBytes?: number;
  readonly now?: () => number;
  readonly fs?: AuditFs;
}

/**
 * One JSON line per tool call (`ABP_MCP_AUDIT=1`): time, profile, tool, outcome, duration and error code.
 * Never arguments or results. Rotates at 5 MB, keeping three old files.
 *
 * Best-effort: a failed write never fails the tool call it describes. The first failure is logged once as a
 * warning; after each failure, writing pauses for a minute. A rotation that another process blocks is skipped
 * and the entry is appended to the current file.
 */
export class AuditLog {
  private readonly fs: AuditFs;
  private readonly now: () => number;
  private pausedUntil = 0;
  private warned = false;

  constructor(private readonly file: string, private readonly profile: string, private readonly options: AuditLogOptions = {}) {
    this.fs = options.fs ?? fs;
    this.now = options.now ?? Date.now;
  }

  /** The audit log for a profile when `ABP_MCP_AUDIT=1`, else `undefined`. */
  static fromEnv(stateDir: string, profile: string, env: NodeJS.ProcessEnv = process.env, log?: Logger): AuditLog | undefined {
    return env.ABP_MCP_AUDIT === "1" || env.ABP_MCP_AUDIT === "true"
      ? new AuditLog(path.join(stateDir, "audit", `${profile}.jsonl`), profile, log ? { log } : {})
      : undefined;
  }

  /** Append one entry. Never throws. */
  record(entry: AuditEntry): void {
    if (this.now() < this.pausedUntil) {
      return;
    }

    try {
      this.fs.mkdirSync(path.dirname(this.file), { recursive: true });
      this.rotateIfFull();
      const line = JSON.stringify({ time: new Date(this.now()).toISOString(), profile: this.profile, ...entry });
      this.fs.appendFileSync(this.file, line + "\n", { mode: 0o600 });
    } catch (error) {
      this.pausedUntil = this.now() + AUDIT_BACKOFF_MS;
      if (this.warned) {
        this.options.log?.debug("Could not write the audit log", error);
      } else {
        this.warned = true;
        this.options.log?.warn(
          `Could not write the audit log ${this.file}. Tool calls are not affected; auditing pauses for a minute after each failure`, error);
      }
    }
  }

  private rotateIfFull(): void {
    let size: number;
    try {
      size = this.fs.statSync(this.file).size;
    } catch {
      return;
    }

    if (size < (this.options.maxBytes ?? MAX_BYTES)) {
      return;
    }

    try {
      for (let index = KEEP - 1; index >= 1; index--) {
        const from = `${this.file}.${index}`;
        if (this.fs.existsSync(from)) {
          this.fs.renameSync(from, `${this.file}.${index + 1}`);
        }
      }

      this.fs.renameSync(this.file, `${this.file}.1`);
    } catch (error) {
      if (!ROTATION_BUSY_CODES.has((error as NodeJS.ErrnoException).code ?? "")) {
        throw error;
      }

      this.options.log?.debug("Could not rotate the audit log (another process holds it); appending to the current file", error);
    }
  }
}

/** The error code an AbpMcp tool error starts with (`[FORBIDDEN] ...`), if any. */
export function toolErrorCode(result: CallToolResult): string | undefined {
  const first = result.content?.[0];
  const text = first && first.type === "text" ? first.text : undefined;
  return text?.match(/^\[([A-Z0-9_:.]+)\]/)?.[1];
}
