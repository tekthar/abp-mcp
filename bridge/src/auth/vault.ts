import fs from "node:fs";
import path from "node:path";
import type { Logger } from "../log.js";
import { KeychainUnavailableError } from "./errors.js";
import { restrictToCurrentWindowsUser } from "./windows-acl.js";

/** The secrets the bridge can keep for a profile. */
export type SecretKind = "refresh_token" | "password" | "client_secret";

/** Somewhere to keep secrets. */
export interface SecretStore {
  readonly description: string;
  get(profile: string, kind: SecretKind): Promise<string | null>;
  set(profile: string, kind: SecretKind, value: string): Promise<void>;
  delete(profile: string, kind: SecretKind): Promise<void>;
}

// Kept from 0.1.0, published as @tekthar/abp-mcp-bridge, so stored sign-ins carry over.
const SERVICE = "abp-mcp-bridge";

/**
 * Keeps a profile's secrets. Refresh tokens go in the OS keychain (macOS Keychain, Windows Credential
 * Manager, libsecret), or, where there is none, an owner-only file. Passwords and client secrets go in the
 * keychain only, and only when the user asks: a file next to its own key would protect nothing.
 */
export class CredentialVault {
  constructor(
    private readonly keychain: SecretStore | undefined,
    private readonly file: SecretStore,
  ) {}

  /**
   * The vault for this machine. `ABP_MCP_KEYCHAIN=off` skips the keychain (refresh tokens then use the file
   * and passwords cannot be saved).
   */
  static async create(stateDir: string, log: Logger, env: NodeJS.ProcessEnv = process.env): Promise<CredentialVault> {
    const file = new FileSecretStore(path.join(stateDir, "credentials"));
    if (env.ABP_MCP_KEYCHAIN === "off") {
      return new CredentialVault(undefined, file);
    }

    return new CredentialVault(await KeychainSecretStore.probe(log), file);
  }

  /** Where refresh tokens are kept, for `doctor`. */
  get refreshTokenStore(): string {
    return (this.keychain ?? this.file).description;
  }

  get hasKeychain(): boolean {
    return this.keychain !== undefined;
  }

  async getRefreshToken(profile: string): Promise<string | null> {
    return (await this.keychain?.get(profile, "refresh_token")) ?? await this.file.get(profile, "refresh_token");
  }

  async setRefreshToken(profile: string, value: string): Promise<void> {
    if (this.keychain) {
      await this.keychain.set(profile, "refresh_token", value);
      await this.file.delete(profile, "refresh_token");
    } else {
      await this.file.set(profile, "refresh_token", value);
    }
  }

  async deleteRefreshToken(profile: string): Promise<void> {
    await this.keychain?.delete(profile, "refresh_token");
    await this.file.delete(profile, "refresh_token");
  }

  /** A saved password or client secret. Never read from a file. */
  async getSecret(profile: string, kind: "password" | "client_secret"): Promise<string | null> {
    return (await this.keychain?.get(profile, kind)) ?? null;
  }

  async setSecret(profile: string, kind: "password" | "client_secret", value: string): Promise<void> {
    if (!this.keychain) {
      throw new KeychainUnavailableError(
        "There is no OS keychain on this machine, so the bridge will not save the " +
        (kind === "password" ? "password" : "client secret") +
        ". Provide it through an environment variable instead (see `abp-mcp --help`).");
    }

    await this.keychain.set(profile, kind, value);
  }

  /** Forget everything stored for a profile. */
  async forget(profile: string): Promise<void> {
    for (const kind of ["refresh_token", "password", "client_secret"] as const) {
      await this.keychain?.delete(profile, kind);
      await this.file.delete(profile, kind);
    }
  }
}

interface KeyringEntry {
  getPassword(): Promise<string | null | undefined>;
  setPassword(value: string): Promise<void>;
  deletePassword(): Promise<boolean>;
}

type KeyringModule = { AsyncEntry: new (service: string, account: string) => KeyringEntry };

/** The OS keychain, through the optional `@napi-rs/keyring` dependency. */
export class KeychainSecretStore implements SecretStore {
  readonly description = "the OS keychain";

  private constructor(private readonly keyring: KeyringModule) {}

  /** The keychain store, or `undefined` when the native module or a keychain service is missing. */
  static async probe(log: Logger): Promise<KeychainSecretStore | undefined> {
    try {
      const keyring = await import("@napi-rs/keyring") as unknown as KeyringModule;
      await new keyring.AsyncEntry(SERVICE, "__probe__").getPassword();
      return new KeychainSecretStore(keyring);
    } catch (error) {
      log.debug("No usable OS keychain; refresh tokens will be kept in an owner-only file", error);
      return undefined;
    }
  }

  async get(profile: string, kind: SecretKind): Promise<string | null> {
    try {
      return (await this.entry(profile, kind).getPassword()) ?? null;
    } catch {
      // A locked or unavailable keychain reads as empty; the caller falls back or asks the user to sign in.
      return null;
    }
  }

  async set(profile: string, kind: SecretKind, value: string): Promise<void> {
    await this.entry(profile, kind).setPassword(value);
  }

  async delete(profile: string, kind: SecretKind): Promise<void> {
    try {
      await this.entry(profile, kind).deletePassword();
    } catch {
      // Nothing stored.
    }
  }

  private entry(profile: string, kind: SecretKind): KeyringEntry {
    return new this.keyring.AsyncEntry(SERVICE, `${profile}:${kind}`);
  }
}

/** One JSON file per profile, readable by the current user only (0600, or a Windows ACL). */
export class FileSecretStore implements SecretStore {
  constructor(private readonly directory: string) {}

  get description(): string {
    return `an owner-only file in ${this.directory}`;
  }

  async get(profile: string, kind: SecretKind): Promise<string | null> {
    return this.read(profile)[kind] ?? null;
  }

  async set(profile: string, kind: SecretKind, value: string): Promise<void> {
    this.write(profile, { ...this.read(profile), [kind]: value });
  }

  async delete(profile: string, kind: SecretKind): Promise<void> {
    const entries = this.read(profile);
    if (kind in entries) {
      delete entries[kind];
      this.write(profile, entries);
    }
  }

  private fileFor(profile: string): string {
    return path.join(this.directory, `${profile}.json`);
  }

  private read(profile: string): Partial<Record<SecretKind, string>> {
    try {
      return JSON.parse(fs.readFileSync(this.fileFor(profile), "utf8")) as Partial<Record<SecretKind, string>>;
    } catch {
      return {};
    }
  }

  private write(profile: string, entries: Partial<Record<SecretKind, string>>): void {
    const file = this.fileFor(profile);
    if (Object.keys(entries).length === 0) {
      fs.rmSync(file, { force: true });
      return;
    }

    fs.mkdirSync(this.directory, { recursive: true, mode: 0o700 });
    const temporary = `${file}.${process.pid}.tmp`;
    try {
      fs.writeFileSync(temporary, JSON.stringify(entries), { mode: 0o600 });
      restrictToCurrentUser(temporary);
      fs.renameSync(temporary, file);
    } finally {
      // Never leave a secret behind in a file whose access could not be restricted.
      fs.rmSync(temporary, { force: true });
    }
  }
}

function restrictToCurrentUser(file: string): void {
  if (process.platform !== "win32") {
    fs.chmodSync(file, 0o600);
    return;
  }

  // Remove inherited entries and grant only the current user (by SID, so Entra ID and domain accounts resolve).
  restrictToCurrentWindowsUser(file);
}
