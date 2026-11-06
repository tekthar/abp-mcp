import assert from "node:assert/strict";
import fs from "node:fs";
import { after, afterEach, before, describe, it } from "node:test";
import { AuditLog } from "../../src/audit.js";
import type { FetchLike } from "../../src/auth/oauth.js";
import { freePort, Harness, textOf, waitFor, type HarnessOptions } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";
import { autoConsent, OAuthStub } from "../helpers/oauth-stub.js";

describe("proxy", () => {
  const oauth = new OAuthStub();
  let host: McpStub;
  let harness: Harness | undefined;
  let port = 0;

  before(async () => {
    await oauth.start();
    port = await freePort();
  });

  after(async () => {
    await oauth.stop();
  });

  afterEach(async () => {
    await harness?.close();
    harness = undefined;
    await host?.stop();
  });

  interface StartOptions {
    discover?: boolean;
    spec?: Record<string, unknown>;
    env?: NodeJS.ProcessEnv;
    audit?: HarnessOptions["audit"];
    signInWaitMs?: number;
    initializeWaitMs?: number;
    baseFetch?: FetchLike;
    openUrl?: (url: string) => Promise<void>;
    /** Runs after the bridge is built and before Claude's initialize. */
    beforeConnect?: (harness: Harness) => Promise<void>;
  }

  async function start(options: StartOptions = {}): Promise<Harness> {
    host = await new McpStub({ isValidToken: (token) => oauth.isValid(token), issuer: oauth.issuer, discover: options.discover ?? true }).start();
    harness = new Harness({
      ...(options.initializeWaitMs !== undefined ? { initializeWaitMs: options.initializeWaitMs } : {}),
      ...(options.baseFetch ? { baseFetch: options.baseFetch } : {}),
      spec: {
        url: host.url,
        auth: { type: "password", clientId: "MyApp_App", authority: oauth.issuer, scope: "openid offline_access Library", username: "alice" },
        ...options.spec,
      },
      env: options.env ?? { ABP_MCP_PASSWORD: "correct horse" },
      ...(options.audit ? { audit: options.audit } : {}),
      ...(options.signInWaitMs !== undefined ? { signInWaitMs: options.signInWaitMs } : {}),
      ...(options.openUrl ? { openUrl: options.openUrl } : {}),
    });
    await options.beforeConnect?.(harness);
    return harness.connect();
  }

  it("lists and calls the host's tools as the signed-in user", async () => {
    const bridge = await start();
    assert.deepEqual(await bridge.listToolNames(), ["Book_Crash", "Book_CrashMidStream", "Book_Delete", "Book_Get", "Book_List", "Book_Slow", "Library_Overview"]);

    const result = await bridge.call("Book_Get");
    assert.equal(textOf(result), "Foundation");
    assert.equal(host.ran("Book_Get"), 1);
    assert.ok(host.authorizations.every((value) => value?.startsWith("Bearer at-")), "every request carried the user's token");
  });

  it("tells Claude the environment, after the host's own instructions", async () => {
    const bridge = await start({ spec: { stage: "production", notes: "Ask before checkouts.", tools: { preset: "all" } } });
    const instructions = bridge.client.getInstructions() ?? "";
    // Regression (#104): initialize waits for the host session, so it carries the host's own information.
    assert.equal(bridge.client.getServerVersion()?.name, "Stub.Library");
    assert.match(instructions, /^Stub library host\.\n/);
    assert.doesNotMatch(instructions, /instructions are missing/);
    assert.match(instructions, /profile 'test' \(production\)/);
    assert.match(instructions, /PRODUCTION/);
    assert.match(instructions, /Ask before checkouts\./);
  });

  it("read-only: lists GET tools only, plus included ones, and refuses the rest without sending them", async () => {
    const bridge = await start({ spec: { stage: "production", tools: { include: ["*_List"] } } });
    assert.deepEqual(await bridge.listToolNames(), ["Book_Get", "Book_List"]);

    const refused = await bridge.call("Book_Delete");
    assert.equal(refused.isError, true);
    assert.match(textOf(refused), /^\[BRIDGE_TOOL_HIDDEN\] .*read-only preset.*not sent to the host/);
    assert.equal(host.ran("Book_Delete"), 0);
  });

  it("read-only: a direct call before any tools/list is still checked", async () => {
    const bridge = await start({ spec: { tools: { preset: "read-only" } } });
    assert.equal((await bridge.call("Book_Delete")).isError, true);
    assert.equal(host.ran("Book_Delete"), 0);
    assert.equal(textOf(await bridge.call("Book_Get")), "Foundation");
  });

  it("read-only without _discover hides every tool", async () => {
    const bridge = await start({ discover: false, spec: { tools: { preset: "read-only" } } });
    assert.deepEqual(await bridge.listToolNames(), []);
    assert.match(textOf(await bridge.call("Book_Get")), /_discover/);
    assert.equal(host.ran("Book_Get"), 0);
  });

  it("deny globs hide tools on list and on call", async () => {
    const bridge = await start({ spec: { tools: { deny: ["*_Delete", "Book_Crash"] } } });
    assert.ok(!(await bridge.listToolNames()).includes("Book_Delete"));
    assert.match(textOf(await bridge.call("book_delete")), /BRIDGE_TOOL_HIDDEN/);
    assert.equal(host.ran("Book_Delete"), 0);
  });

  it("passes the host's tool errors through unchanged and audits them without arguments", async () => {
    const bridge = await start({ audit: true });
    const result = await bridge.call("Library_Overview", { secretArgument: "do-not-log" });
    assert.equal(result.isError, true);
    assert.equal(textOf(result), "[FORBIDDEN] Missing permission: Library.Overview");

    const audit = fs.readFileSync(bridge.auditFile, "utf8");
    assert.match(audit, /"outcome":"tool_error"/);
    assert.match(audit, /"errorCode":"FORBIDDEN"/);
    assert.doesNotMatch(audit, /do-not-log/);
  });

  it("renews an expired token after a 401 and sends the call once", async () => {
    const bridge = await start();
    await bridge.listToolNames();
    oauth.expireAccessTokens();
    const grants = oauth.tokenRequests.length;

    const result = await bridge.call("Book_Delete");
    assert.equal(result.isError ?? false, false);
    assert.equal(host.ran("Book_Delete"), 1);
    assert.equal(oauth.grantTypes.slice(grants).at(0), "refresh_token");
  });

  it("reconnects when the host ended the session, and sends the call once", async () => {
    const bridge = await start();
    await bridge.listToolNames();
    await host.expireSessions();

    assert.equal(textOf(await bridge.call("Book_Delete")), "deleted");
    assert.equal(host.ran("Book_Delete"), 1);
  });

  it("never replays a call whose connection dropped, and says it may have run", async () => {
    const bridge = await start({ audit: true });
    await bridge.listToolNames();

    const result = await bridge.call("Book_Crash");
    assert.equal(result.isError, true);
    assert.match(textOf(result), /^\[BRIDGE_CALL_OUTCOME_UNKNOWN\] .*may have run/);
    await new Promise((resolve) => setTimeout(resolve, 300));
    assert.equal(host.ran("Book_Crash"), 1, "the host ran it exactly once");

    // The session is still usable afterwards.
    assert.equal(textOf(await bridge.call("Book_Get")), "Foundation");
  });

  it("never replays a call whose answer stream broke, and does not wait for the timeout", async () => {
    const bridge = await start();
    await bridge.listToolNames();

    const started = Date.now();
    const result = await bridge.call("Book_CrashMidStream");
    assert.match(textOf(result), /^\[BRIDGE_CALL_OUTCOME_UNKNOWN\] .*may have run/);
    assert.ok(Date.now() - started < 5_000, "the broken stream is noticed at once, not after the call timeout");
    await new Promise((resolve) => setTimeout(resolve, 300));
    assert.equal(host.ran("Book_CrashMidStream"), 1, "the host ran it exactly once");
  });

  it("reports an unreachable host as not sent", async () => {
    const bridge = await start();
    await bridge.listToolNames();
    await host.stop();
    await bridge.upstream.close();

    const result = await bridge.call("Book_Get");
    assert.match(textOf(result), /^\[BRIDGE_UNREACHABLE\] .*did not run/);
  });

  it("passes tools/list_changed through", async () => {
    const bridge = await start();
    await bridge.listToolNames();
    await waitFor(() => host.authorizations.length > 0);
    // The client opens its notification stream after initialize; give it a moment.
    await new Promise((resolve) => setTimeout(resolve, 200));
    await host.notifyToolListChanged();
    await waitFor(() => bridge.toolListChanges.length > 0);
  });

  it("forwards cancellation to the host", async () => {
    const bridge = await start({ audit: true });
    await bridge.listToolNames();
    const controller = new AbortController();
    const call = bridge.call("Book_Slow", {}, controller.signal);
    await waitFor(() => host.ran("Book_Slow") === 1);
    controller.abort("user cancelled");
    await assert.rejects(call);
    await waitFor(() => fs.existsSync(bridge.auditFile) && /"cancelled"/.test(fs.readFileSync(bridge.auditFile, "utf8")));
  });

  it("browser: the first tools/list signs in and lists the tools", async () => {
    const bridge = await start({
      spec: { auth: { type: "browser", clientId: "MyApp_Mcp", callbackPorts: [port] } },
      env: {},
      openUrl: autoConsent,
    });
    assert.ok((await bridge.listToolNames()).includes("Book_Get"));
  });

  it("browser: a first run signs in during initialize, so the host's instructions reach Claude (#104)", async () => {
    const authorizes = oauth.authorizeRequests.length;
    const bridge = await start({
      spec: { auth: { type: "browser", clientId: "MyApp_Mcp", callbackPorts: [port] } },
      env: {},
      initializeWaitMs: 5_000,
      openUrl: async (url) => {
        setTimeout(() => void autoConsent(url), 300);
      },
    });

    assert.equal(bridge.client.getServerVersion()?.name, "Stub.Library");
    assert.match(bridge.client.getInstructions() ?? "", /^Stub library host\./);
    assert.ok((await bridge.listToolNames()).includes("Book_Get"));
    assert.equal(oauth.authorizeRequests.length - authorizes, 1, "one browser sign-in, shared by initialize and tools/list");
  });

  it("browser: a sign-in that outlasts initialize gets the bridge's own info, then announces the tools", async () => {
    const bridge = await start({
      spec: { auth: { type: "browser", clientId: "MyApp_Mcp", callbackPorts: [port] } },
      env: {},
      signInWaitMs: 50,
      initializeWaitMs: 50,
      openUrl: async (url) => {
        setTimeout(() => void autoConsent(url), 400);
      },
    });

    assert.equal(bridge.client.getServerVersion()?.name, "abp-mcp");
    assert.match(bridge.client.getInstructions() ?? "", /application's own instructions are missing from this session/);
    assert.deepEqual(await bridge.listToolNames(), []);
    await waitFor(() => bridge.toolListChanges.length > 0);
    assert.ok((await bridge.listToolNames()).includes("Book_Get"));
  });

  it("a host that comes up late still gets its instructions into initialize (#104)", async () => {
    const hostPort = await freePort();
    harness = new Harness({
      spec: {
        url: `http://127.0.0.1:${hostPort}/mcp`,
        auth: { type: "password", clientId: "MyApp_App", authority: oauth.issuer, scope: "openid offline_access Library", username: "alice" },
      },
      env: { ABP_MCP_PASSWORD: "correct horse" },
      initializeWaitMs: 10_000,
    });
    const late = setTimeout(() => {
      host = new McpStub({ isValidToken: (token) => oauth.isValid(token), issuer: oauth.issuer, discover: true, port: hostPort });
      void host.start();
    }, 700);

    try {
      const bridge = await harness.connect();
      assert.equal(bridge.client.getServerVersion()?.name, "Stub.Library");
      assert.match(bridge.client.getInstructions() ?? "", /^Stub library host\./);
    } finally {
      clearTimeout(late);
    }
  });

  it("an unreachable host: initialize answers within the wait, with the bridge's own info and a note (#104)", async () => {
    const started = Date.now();
    harness = new Harness({
      spec: { url: `http://127.0.0.1:${await freePort()}/mcp`, auth: { type: "none" } },
      initializeWaitMs: 300,
    });
    const bridge = await harness.connect();
    assert.ok(Date.now() - started < 2_000, "initialize was not held past the wait");
    assert.equal(bridge.client.getServerVersion()?.name, "abp-mcp");
    assert.match(bridge.client.getInstructions() ?? "", /could not reach 127\.0\.0\.1:\d+ .*instructions are missing/);
  });

  it("a failed audit write never turns a completed call into an error (#102)", async () => {
    const bridge = await start({
      // The audit file's path is a directory: every append fails.
      audit: (file, log) => {
        fs.mkdirSync(file, { recursive: true });
        return new AuditLog(file, "test", { log });
      },
    });

    assert.equal(textOf(await bridge.call("Book_Delete")), "deleted");
    assert.equal((await bridge.call("Library_Overview")).isError, true);
    assert.equal(textOf(await bridge.call("Book_Get")), "Foundation");
    assert.equal(host.ran("Book_Delete"), 1, "the host ran it exactly once");
    assert.equal(bridge.lines.filter((line) => / WARN Could not write the audit log/.test(line)).length, 1, "one warning");
  });

  it("an audit sink that throws never turns a completed call into an error (#102)", async () => {
    const bridge = await start({
      audit: () => ({ record: () => { throw new Error("disk full"); } }) as unknown as AuditLog,
    });

    const result = await bridge.call("Book_Delete");
    assert.equal(result.isError ?? false, false);
    assert.equal(textOf(result), "deleted");
    assert.equal(host.ran("Book_Delete"), 1, "the host ran it exactly once");
  });

  it("two requests failing on the same expired session reconnect once, not twice (#103)", async () => {
    let releaseLate!: () => void;
    const lateGate = new Promise<void>((resolve) => {
      releaseLate = resolve;
    });
    let held = false;
    let heldResponse!: () => void;
    const responseHeld = new Promise<void>((resolve) => {
      heldResponse = resolve;
    });
    const bridge = await start({
      // Hold the first 404 for Book_Get until the other request has reconnected and finished.
      baseFetch: async (url, init) => {
        const response = await fetch(url, init);
        if (!held && response.status === 404 && typeof init?.body === "string" && init.body.includes("\"Book_Get\"")) {
          held = true;
          heldResponse();
          await lateGate;
        }

        return response;
      },
    });
    await bridge.listToolNames();
    const opened = host.sessionsOpened;
    await host.expireSessions();

    const late = bridge.call("Book_Get");
    await responseHeld;
    assert.equal(textOf(await bridge.call("Book_Delete")), "deleted");
    releaseLate();
    assert.equal(textOf(await late), "Foundation");

    assert.equal(host.sessionsOpened - opened, 1, "the late failure reused the new session instead of replacing it");
    assert.equal(host.ran("Book_Get"), 1);
    assert.equal(host.ran("Book_Delete"), 1);
  });

  it("browser: an expired stored sign-in does not hold tools/list while the browser is open", async () => {
    const bridge = await start({
      spec: { auth: { type: "browser", clientId: "MyApp_Mcp", callbackPorts: [port] } },
      env: {},
      signInWaitMs: 50,
      initializeWaitMs: 50,
      openUrl: async (url) => {
        setTimeout(() => void autoConsent(url), 400);
      },
      beforeConnect: async (harness) => harness.vault.setRefreshToken("test", "rt-revoked-long-ago"),
    });

    const started = Date.now();
    assert.deepEqual(await bridge.listToolNames(), []);
    assert.ok(Date.now() - started < 300, "tools/list answered without waiting for the browser");
    await waitFor(() => bridge.toolListChanges.length > 0);
    assert.ok((await bridge.listToolNames()).includes("Book_Get"));
  });

  it("never writes a token, password or code to the debug log", async () => {
    const bridge = await start({ env: { ABP_MCP_PASSWORD: "correct horse" } });
    await bridge.listToolNames();
    oauth.expireAccessTokens();
    await bridge.call("Book_Get");
    await bridge.call("Book_Crash");

    const log = fs.readFileSync(bridge.logFile, "utf8") + bridge.lines.join("");
    assert.ok(log.length > 0, "debug logging produced output");
    for (const secret of [...oauth.issued, "correct horse"]) {
      assert.ok(!log.includes(secret), `the log contains ${secret.slice(0, 6)}...`);
    }
  });
});
