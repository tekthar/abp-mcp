import fs from "node:fs";
import path from "node:path";
import { BreakerOpenError } from "./errors.js";

export const BREAKER_PAUSE_MS = 15 * 60 * 1000;

interface BreakerState {
  readonly openUntil: number;
  readonly reason: string;
}

/**
 * Pauses sign-in for a profile after the authorization server rejects its credentials. ABP locks an account
 * after a few failed sign-ins, and Claude restarts the bridge often, so the pause is kept on disk: one wrong
 * password costs one failed attempt, not a locked account.
 */
export class SignInBreaker {
  constructor(
    private readonly file: string,
    private readonly profile: string,
    private readonly now: () => number = Date.now,
  ) {}

  /** For a profile, its breaker file under `stateDir`. */
  static forProfile(stateDir: string, profile: string, now?: () => number): SignInBreaker {
    return new SignInBreaker(path.join(stateDir, "breaker", `${profile}.json`), profile, now);
  }

  /** Throws {@link BreakerOpenError} while the pause lasts. */
  check(): void {
    const state = this.read();
    if (state && state.openUntil > this.now()) {
      throw new BreakerOpenError(new Date(state.openUntil), state.reason, this.profile);
    }
  }

  /** Start a pause. */
  trip(reason: string): void {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const state: BreakerState = { openUntil: this.now() + BREAKER_PAUSE_MS, reason };
    fs.writeFileSync(this.file, JSON.stringify(state), { mode: 0o600 });
  }

  /** End any pause, e.g. after the user signed in successfully. */
  reset(): void {
    fs.rmSync(this.file, { force: true });
  }

  /** The current pause, if any. */
  status(): { open: false } | { open: true; until: Date; reason: string } {
    const state = this.read();
    return state && state.openUntil > this.now()
      ? { open: true, until: new Date(state.openUntil), reason: state.reason }
      : { open: false };
  }

  private read(): BreakerState | undefined {
    try {
      const state = JSON.parse(fs.readFileSync(this.file, "utf8")) as Partial<BreakerState>;
      return typeof state.openUntil === "number" && typeof state.reason === "string"
        ? { openUntil: state.openUntil, reason: state.reason }
        : undefined;
    } catch {
      return undefined;
    }
  }
}
