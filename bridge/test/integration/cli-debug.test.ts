import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { after, before, describe, it } from "node:test";
import { freePort, tempDir } from "../helpers/harness.js";
import { McpStub } from "../helpers/mcp-stub.js";
import { OAuthStub } from "../helpers/oauth-stub.js";

const BIN = fileURLToPath(new URL("../../src/bin.js", import.meta.url));

/** The bridge opens the browser with xdg-open (Linux) or open (macOS); Windows goes through rundll32. */
const canFakeBrowser = process.platform === "linux" || process.platform === "darwin";

/**
 * A fake `xdg-open`/`open` for the child: follows the authorize redirect to the bridge's loopback callback,
 * as a browser that auto-consents would.
 */
function writeFakeBrowser(dir: string): void {
  const script = `#!${process.execPath}
(async () => {
  const authorize = await fetch(process.argv[2], { redirect: "manual" });
  await (await fetch(authorize.headers.get("location"))).text();
})();
`;
  for (const name of ["xdg-open", "open"]) {
    fs.writeFileSync(path.join(dir, name), script, { mode: 0o755 });
  }
}

function runCli(args: string[], env: NodeJS.ProcessEnv): Promise<{ code: number; output: string }> {
  return new Promise((resolve) => {
    execFile(process.execPath, [BIN, ...args], { env, timeout: 30_000 }, (error, stdout, stderr) => {
      resolve({ code: error ? (typeof error.code === "number" ? error.code : 1) : 0, output: stdout + stderr });
    });
  });
}

describe("ABP_MCP_DEBUG=1 through the real CLI (#107)", () => {
  const oauth = new OAuthStub({ clientSecret: "cs-very-secret-value" });
  let host: McpStub;
  const temp = tempDir();
  const fakeBin = path.join(temp.dir, "bin");
  const logFile = path.join(temp.dir, "debug.log");
  let env: NodeJS.ProcessEnv;

  before(async () => {
    await oauth.start();
    host = await new McpStub({ isValidToken: (token) => oauth.isValid(token), issuer: oauth.issuer, discover: true }).start();
    fs.mkdirSync(fakeBin);
    writeFakeBrowser(fakeBin);
    const port = await freePort();
    const profiles = path.join(temp.dir, "profiles.json");
    fs.writeFileSync(profiles, JSON.stringify({
      profiles: {
        web: {
          url: host.url,
          auth: { type: "browser", clientId: "MyApp_Mcp", authority: oauth.issuer, scope: "openid offline_access Library", callbackPorts: [port] },
        },
        robot: { url: host.url, auth: { type: "client_credentials", clientId: "Robot", authority: oauth.issuer, scope: "Library" } },
      },
    }));
    env = {
      PATH: `${fakeBin}${path.delimiter}${process.env.PATH ?? ""}`,
      HOME: temp.dir,
      ABP_MCP_PROFILES: profiles,
      ABP_MCP_CONFIG_DIR: path.join(temp.dir, "config"),
      ABP_MCP_KEYCHAIN: "off",
      ABP_MCP_DEBUG: "1",
      ABP_MCP_LOG_FILE: logFile,
    };
  });

  after(async () => {
    await host.stop();
    await oauth.stop();
    temp.cleanup();
  });

  function assertClean(output: string, secrets: string[]): void {
    const log = fs.readFileSync(logFile, "utf8") + output;
    assert.ok(log.length > 0, "debug logging produced output");
    for (const secret of secrets) {
      assert.ok(secret.length > 0);
      assert.ok(!log.includes(secret), `the log contains ${secret.slice(0, 4)}...`);
    }
  }

  it("browser login: no authorization code, verifier or token in the log", { skip: !canFakeBrowser && "needs xdg-open or open" }, async () => {
    const before = oauth.tokenRequests.length;
    const { code, output } = await runCli(["login", "web"], env);
    assert.equal(code, 0, output);
    assert.match(output, /Signed in to 'web'/);

    const exchange = oauth.tokenRequests.slice(before).find((request) => request.grant_type === "authorization_code");
    assert.ok(exchange?.code && exchange.code_verifier, "the CLI went through the code flow");
    assertClean(output, [exchange.code, exchange.code_verifier, ...oauth.issued]);
  });

  it("client_credentials login and doctor: no client secret or token in the log", async () => {
    const { code, output } = await runCli(["login", "robot"], { ...env, ABP_MCP_CLIENT_SECRET: "cs-very-secret-value" });
    assert.equal(code, 0, output);
    const doctor = await runCli(["doctor", "robot"], { ...env, ABP_MCP_CLIENT_SECRET: "cs-very-secret-value" });
    assert.equal(doctor.code, 0, doctor.output);

    assert.ok(oauth.tokenRequests.some((request) => request.client_secret === "cs-very-secret-value"));
    assertClean(output + doctor.output, ["cs-very-secret-value", ...oauth.issued]);
    assert.match(fs.readFileSync(logFile, "utf8"), /DEBUG/, "debug lines were written");
  });
});
