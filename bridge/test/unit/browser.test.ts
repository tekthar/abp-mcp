import assert from "node:assert/strict";
import net from "node:net";
import { describe, it } from "node:test";
import { browserSignIn, listenOnFirstFreePort } from "../../src/auth/browser.js";
import { Logger } from "../../src/log.js";
import { Redactor } from "../../src/redact.js";
import { freePort } from "../helpers/harness.js";

async function ipv6Available(): Promise<boolean> {
  return await new Promise((resolve) => {
    const server = net.createServer();
    server.once("error", () => resolve(false));
    server.listen(0, "::1", () => server.close(() => resolve(true)));
  });
}

const hasIpv6 = await ipv6Available();
const log = new Logger({ redactor: new Redactor(), sink: () => undefined });

/** A browser stand-in that answers the sign-in at `callbackHost` instead of the redirect URI's host. */
function answerAt(callbackHost: string) {
  return async (url: string): Promise<void> => {
    const authorize = new URL(url);
    const redirect = new URL(authorize.searchParams.get("redirect_uri")!);
    const callback = new URL(`http://${callbackHost}:${redirect.port}/callback`);
    callback.searchParams.set("code", "the-code");
    callback.searchParams.set("state", authorize.searchParams.get("state")!);
    await (await fetch(callback)).text();
  };
}

describe("browser sign-in callback (#105)", () => {
  it("with redirectHost localhost, still listens on 127.0.0.1", async () => {
    const port = await freePort();
    const result = await browserSignIn({
      authorizationEndpoint: "https://auth.example.com/connect/authorize", clientId: "c", ports: [port],
      redirectHost: "localhost", profileName: "p", openUrl: answerAt("127.0.0.1"), log, timeoutMs: 5_000,
    });
    assert.equal(result.code, "the-code");
    assert.equal(result.redirectUri, `http://localhost:${port}/callback`);
  });

  it("with redirectHost localhost, also answers a browser that resolves localhost to ::1", { skip: !hasIpv6 && "no IPv6 loopback here" }, async () => {
    const port = await freePort();
    const result = await browserSignIn({
      authorizationEndpoint: "https://auth.example.com/connect/authorize", clientId: "c", ports: [port],
      redirectHost: "localhost", profileName: "p", openUrl: answerAt("[::1]"), log, timeoutMs: 5_000,
    });
    assert.equal(result.code, "the-code");

    // Both listeners are closed afterwards.
    const { server } = await listenOnFirstFreePort([port], "localhost");
    server.close();
  });

  it("with redirectHost 127.0.0.1, listens on IPv4 only", { skip: !hasIpv6 && "no IPv6 loopback here" }, async () => {
    const port = await freePort();
    const { server } = await listenOnFirstFreePort([port], "127.0.0.1");
    try {
      await assert.rejects(fetch(`http://[::1]:${port}/callback`));
    } finally {
      server.close();
    }
  });

  it("moves on to the next port when the first is taken", async () => {
    const taken = await listenOnFirstFreePort([0], "localhost");
    const busy = (taken.server.address() as net.AddressInfo).port;
    const next = await freePort();
    try {
      const { server, port } = await listenOnFirstFreePort([busy, next], "localhost");
      assert.equal(port, next);
      server.close();
    } finally {
      taken.server.close();
    }
  });
});
