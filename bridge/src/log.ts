import fs from "node:fs";
import path from "node:path";
import type { Redactor } from "./redact.js";

export type LogLevel = "debug" | "info" | "warn" | "error";

export interface LoggerOptions {
  readonly redactor: Redactor;
  /** Log debug lines too (`ABP_MCP_DEBUG=1`). */
  readonly debug?: boolean;
  /** Also append every line to this file (`ABP_MCP_LOG_FILE`). */
  readonly file?: string;
  /** Where lines go. Defaults to stderr: in stdio mode stdout carries only the MCP protocol. */
  readonly sink?: (line: string) => void;
}

/** A small logger that writes redacted lines to stderr and, optionally, a file. */
export class Logger {
  private readonly sink: (line: string) => void;

  constructor(private readonly options: LoggerOptions) {
    this.sink = options.sink ?? ((line) => process.stderr.write(line));
    if (options.file) {
      fs.mkdirSync(path.dirname(options.file), { recursive: true });
    }
  }

  /** Build a logger from the environment (`ABP_MCP_DEBUG`, `ABP_MCP_LOG_FILE`). */
  static fromEnv(redactor: Redactor, env: NodeJS.ProcessEnv = process.env): Logger {
    return new Logger({
      redactor,
      debug: env.ABP_MCP_DEBUG === "1" || env.ABP_MCP_DEBUG === "true",
      ...(env.ABP_MCP_LOG_FILE ? { file: env.ABP_MCP_LOG_FILE } : {}),
    });
  }

  get isDebug(): boolean {
    return this.options.debug === true;
  }

  debug(message: string, error?: unknown): void {
    this.write("debug", message, error);
  }

  info(message: string, error?: unknown): void {
    this.write("info", message, error);
  }

  warn(message: string, error?: unknown): void {
    this.write("warn", message, error);
  }

  error(message: string, error?: unknown): void {
    this.write("error", message, error);
  }

  private write(level: LogLevel, message: string, error?: unknown): void {
    if (level === "debug" && !this.isDebug) {
      return;
    }

    const detail = error === undefined ? "" : `: ${describeError(error, this.isDebug)}`;
    const line = this.options.redactor.redact(`${new Date().toISOString()} abp-mcp ${level.toUpperCase()} ${message}${detail}`) + "\n";
    try {
      this.sink(line);
      if (this.options.file) {
        fs.appendFileSync(this.options.file, line, { mode: 0o600 });
      }
    } catch {
      // Logging must never break the bridge.
    }
  }
}

/** An error's message and its causes, one per level; with the stack when debugging. */
export function describeError(error: unknown, withStack = false): string {
  const parts: string[] = [];
  let current: unknown = error;
  for (let depth = 0; current !== undefined && current !== null && depth < 5; depth++) {
    if (current instanceof Error) {
      parts.push(withStack && depth === 0 && current.stack ? current.stack : current.message);
      current = (current as { cause?: unknown }).cause;
    } else {
      parts.push(String(current));
      break;
    }
  }

  return parts.join(" <- ");
}
