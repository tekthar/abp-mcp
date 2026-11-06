import assert from "node:assert/strict";
import path from "node:path";
import { describe, it } from "node:test";
import { CredentialVault, ENTRY_OPTIONS, FileSecretStore, KeychainSecretStore, type KeyringModule } from "../../src/auth/vault.js";
import { Logger } from "../../src/log.js";
import { Redactor } from "../../src/redact.js";
import { tempDir } from "../helpers/harness.js";

/**
 * `@napi-rs/keyring` on Linux, as its README documents it: the Secret Service when one runs, else a silent fallback
 * to the kernel keyutils keyring, which a reboot clears. Pinned with `{ linux: { store: "secret-service" } }`, the
 * constructor throws instead of falling back.
 */
function linuxKeyring(secretService: boolean) {
  const stores = { secretService: new Map<string, string>(), kernel: new Map<string, string>() };
  const options: unknown[] = [];
  class AsyncEntry {
    private readonly store: Map<string, string>;
    constructor(private readonly service: string, private readonly account: string, entryOptions?: { linux?: { store?: string } }) {
      options.push(entryOptions);
      if (entryOptions?.linux?.store === "secret-service" && !secretService) {
        throw new Error("Couldn't access platform storage: no Secret Service");
      }

      this.store = secretService ? stores.secretService : stores.kernel;
    }

    async getPassword(): Promise<string | null> {
      return this.store.get(`${this.service}/${this.account}`) ?? null;
    }

    async setPassword(value: string): Promise<void> {
      this.store.set(`${this.service}/${this.account}`, value);
    }

    async deletePassword(): Promise<boolean> {
      return this.store.delete(`${this.service}/${this.account}`);
    }
  }

  return { module: { AsyncEntry } as KeyringModule, stores, options, reboot: () => stores.kernel.clear() };
}

const log = new Logger({ redactor: new Redactor(), sink: () => undefined });

describe("OS keychain on Linux", () => {
  it("does not use the kernel keyring when no Secret Service runs, so a sign-in survives a reboot", async () => {
    const keyring = linuxKeyring(false);
    const keychain = await KeychainSecretStore.probe(log, async () => keyring.module);
    assert.equal(keychain, undefined, "no usable keychain: the kernel keyring is not one");

    const temp = tempDir();
    try {
      const vault = new CredentialVault(keychain, new FileSecretStore(path.join(temp.dir, "credentials")));
      await vault.setRefreshToken("server", "rt-123456");
      keyring.reboot();
      assert.equal(await vault.getRefreshToken("server"), "rt-123456", "the token is in the owner-only file");
      assert.equal(keyring.stores.kernel.size, 0, "nothing was written to the kernel keyring");
    } finally {
      temp.cleanup();
    }
  });

  it("uses the Secret Service when one runs, pinning every entry to it", async () => {
    const keyring = linuxKeyring(true);
    const keychain = await KeychainSecretStore.probe(log, async () => keyring.module);
    assert.ok(keychain);

    await keychain.set("desktop", "refresh_token", "rt-abcdef");
    assert.equal(await keychain.get("desktop", "refresh_token"), "rt-abcdef");
    assert.equal(keyring.stores.secretService.size, 1);
    assert.ok(keyring.options.length >= 3);
    assert.ok(keyring.options.every((value) => value === ENTRY_OPTIONS), "the probe and every entry carry the option");
    assert.deepEqual(ENTRY_OPTIONS, { linux: { store: "secret-service" } });
  });
});
