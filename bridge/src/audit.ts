import fs from "node:fs";
import path from "node:path";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export type AuditOutcome = "ok" | "tool_error" | "refused" | "failed" | "cancelled";

export interface AuditEntry {
  readonly tool: string;
  readonly outcome: AuditOutcome;
  readonly durationMs: number;
  readonly errorCode?: string;
}

const MAX_BYTES = 5 * 1024 * 1024;
const KEEP = 3;

/**
 * One JSON line per tool call (`ABP_MCP_AUDIT=1`): time, profile, tool, outcome, duration and error code.
 * Never arguments or results. Rotates at 5 MB, keeping three old files.
 */
export class AuditLog {
  constructor(private readonly file: string, private readonly profile: string) {}

  /** The audit log for a profile when `ABP_MCP_AUDIT=1`, else `undefined`. */
  static fromEnv(stateDir: string, profile: string, env: NodeJS.ProcessEnv = process.env): AuditLog | undefined {
    return env.ABP_MCP_AUDIT === "1" || env.ABP_MCP_AUDIT === "true"
      ? new AuditLog(path.join(stateDir, "audit", `${profile}.jsonl`), profile)
      : undefined;
  }

  record(entry: AuditEntry): void {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    this.rotateIfFull();
    const line = JSON.stringify({ time: new Date().toISOString(), profile: this.profile, ...entry });
    fs.appendFileSync(this.file, line + "\n", { mode: 0o600 });
  }

  private rotateIfFull(): void {
    let size: number;
    try {
      size = fs.statSync(this.file).size;
    } catch {
      return;
    }

    if (size < MAX_BYTES) {
      return;
    }

    for (let index = KEEP - 1; index >= 1; index--) {
      const from = `${this.file}.${index}`;
      if (fs.existsSync(from)) {
        fs.renameSync(from, `${this.file}.${index + 1}`);
      }
    }

    fs.renameSync(this.file, `${this.file}.1`);
  }
}

/** The error code an AbpMcp tool error starts with (`[FORBIDDEN] ...`), if any. */
export function toolErrorCode(result: CallToolResult): string | undefined {
  const first = result.content?.[0];
  const text = first && first.type === "text" ? first.text : undefined;
  return text?.match(/^\[([A-Z0-9_:.]+)\]/)?.[1];
}
