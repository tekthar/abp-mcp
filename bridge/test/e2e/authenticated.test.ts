import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { after, before, describe, it } from "node:test";
import { setTimeout as sleep } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { CallToolResultSchema, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { discoverSignIn } from "../../src/auth/oauth.js";
import { freePort, tempDir } from "../helpers/harness.js";
import { OAuthStub } from "../helpers/oauth-stub.js";

/**
 * #112: the bridge against the Library sample running authenticated, set up the way docs/oauth.md describes
 * (JwtBearer, the MCP SDK's RFC 9728 metadata and challenge, AbpMcpOptions.AuthorizationPolicy), with this
 * process's stub authorization server issuing RS256 JWTs. Permission grants live in the host, not the token.
 *
 * Start the sample with:
 *   Sample__Auth__Issuer=http://127.0.0.1:<issuer port>
 *   Sample__Auth__Resource=<ABP_MCP_E2E_AUTH_URL>
 *   Sample__Auth__Grants=<ALICE>:Library.Members.Delete
 * then set ABP_MCP_E2E_AUTH_URL to its /mcp URL and ABP_MCP_E2E_AUTH_ISSUER_PORT to the issuer's port.
 */
const hostUrl = process.env.ABP_MCP_E2E_AUTH_URL;
const issuerPort = Number(process.env.ABP_MCP_E2E_AUTH_ISSUER_PORT ?? "0");
const bin = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../dist/bin.js");

/** The user ids the CI job grants (Alice) and does not grant (Bob) Library.Members.Delete. */
export const ALICE = "3a1f0c9e-0d4b-4c5e-9b7a-2f6d8e1c4a01";
const BOB = "7c2e5b1d-8f3a-4e6b-a1c9-5d0f2b7e9c02";
const BOB_PASSWORD = "bob-correct-horse";

/** A token lives 63 s and the bridge renews 60 s before expiry, so it is fresh for about 3 s. */
const TOKEN_LIFETIME_SECONDS = 63;

describe("end to end, authenticated (#112)", {
  skip: hostUrl && issuerPort ? false : "ABP_MCP_E2E_AUTH_URL and ABP_MCP_E2E_AUTH_ISSUER_PORT are not set",
}, () => {
  const oauth = new OAuthStub({
    users: { alice: { sub: ALICE }, bob: { sub: BOB, password: BOB_PASSWORD } },
    jwt: { audience: "LibraryMcp" },
    accessTokenLifetimeSeconds: TOKEN_LIFETIME_SECONDS,
  });
  const temp = tempDir();
  const fakeBin = path.join(temp.dir, "bin");
  let env: Record<string, string>;

  before(async () => {
    await oauth.start(issuerPort);
    fs.mkdirSync(fakeBin);
    writeFakeBrowser(fakeBin);

    // Neither profile sets auth.authority or auth.scope: both come from the host's RFC 9728 metadata.
    const profiles = path.join(temp.dir, "profiles.json");
    fs.writeFileSync(profiles, JSON.stringify({
      profiles: {
        alice: { url: hostUrl, auth: { type: "browser", clientId: "Library_Claude", username: "alice", callbackPorts: [await freePort()] } },
        bob: { url: hostUrl, auth: { type: "password", clientId: "Library_Claude", username: "bob" } },
      },
    }));
    env = {
      ...process.env,
      PATH: `${fakeBin}${path.delimiter}${process.env.PATH ?? ""}`,
      ABP_MCP_PROFILES: profiles,
      ABP_MCP_CONFIG_DIR: path.join(temp.dir, "config"),
      ABP_MCP_KEYCHAIN: "off",
      ABP_MCP_PASSWORD: BOB_PASSWORD,
    } as Record<string, string>;
  });

  after(async () => {
    await oauth.stop();
    temp.cleanup();
  });

  async function connect(profile: string): Promise<Client> {
    const client = new Client({ name: "e2e-claude", version: "1.0.0" });
    await client.connect(new StdioClientTransport({ command: process.execPath, args: [bin, "--profile", profile], env, stderr: "ignore" }));
    return client;
  }

  async function call(client: Client, name: string, args: Record<string, unknown> = {}): Promise<CallToolResult> {
    return await client.request({ method: "tools/call", params: { name, arguments: args } }, CallToolResultSchema) as CallToolResult;
  }

  const text = (result: CallToolResult): string => (result.content[0]?.type === "text" ? result.content[0].text : "");

  it("discovery passes the https, resource and issuer checks against what AddMcp serves", async () => {
    const target = await discoverSignIn(fetch, hostUrl!);
    assert.equal(target.resource, hostUrl);
    assert.equal(target.server.issuer, oauth.issuer);
    assert.equal(target.server.tokenEndpoint, `${oauth.issuer}/connect/token`);
    assert.deepEqual([...(target.resourceScopes ?? [])].sort(), ["LibraryMcp", "offline_access"]);
  });

  it("browser profile: signs in once, calls a permission-gated tool, and renews silently after expiry", async () => {
    const login = await runCli(["login", "alice"], env);
    assert.equal(login.code, 0, login.output);
    assert.equal(oauth.authorizeRequests.length, 1, "the browser opened once");
    assert.equal(oauth.authorizeRequests[0]?.scope, "LibraryMcp offline_access", "the scope came from the host's metadata");

    const client = await connect("alice");
    try {
      const names = (await client.listTools()).tools.map((tool) => tool.name);
      assert.ok(names.includes("Member_Delete"), "Alice holds Library.Members.Delete");

      const registered = await call(client, "Member_Register", { input: { name: "Ada Lovelace", email: "ada@example.org" } });
      assert.notEqual(registered.isError, true, text(registered));
      const { id } = JSON.parse(text(registered)) as { id: string };

      const deleted = await call(client, "Member_Delete", { id });
      assert.notEqual(deleted.isError, true, text(deleted));

      // Let the access token reach the bridge's renewal window, then call again.
      const refreshesBefore = oauth.grantTypes.filter((type) => type === "refresh_token").length;
      await sleep(4_000);
      const listed = await call(client, "Member_List");
      assert.notEqual(listed.isError, true, text(listed));
      assert.ok(oauth.grantTypes.filter((type) => type === "refresh_token").length > refreshesBefore, "the token was renewed");
      assert.equal(oauth.authorizeRequests.length, 1, "renewal never opened the browser");
    } finally {
      await client.close();
    }
  });

  it("a user without Library.Members.Delete neither sees Member_Delete nor can call it", async () => {
    const client = await connect("bob");
    try {
      const names = (await client.listTools()).tools.map((tool) => tool.name);
      assert.ok(names.includes("Member_List"), "Bob is signed in and sees the ungated tools");
      assert.ok(!names.includes("Member_Delete"), "Member_Delete is hidden from Bob");

      const result = await call(client, "Member_Delete", { id: "00000000-0000-0000-0000-000000000001" });
      assert.equal(result.isError, true);
      assert.match(text(result), /^\[FORBIDDEN\]/);
      assert.match(text(result), /Library\.Members\.Delete/);
    } finally {
      await client.close();
    }
  });

  it("doctor passes every step against the host, including the protected-resource metadata", async () => {
    const doctor = await runCli(["doctor", "alice"], env);
    assert.equal(doctor.code, 0, doctor.output);
    assert.match(doctor.output, /^✓ Protected-resource metadata/m);
    assert.match(doctor.output, /^✓ tools\/list/m);
    assert.doesNotMatch(doctor.output, /^[✗!] /m, "no step failed or warned (Library.Members.Delete is defined)");
  });
});

function runCli(args: string[], env: NodeJS.ProcessEnv): Promise<{ code: number; output: string }> {
  return new Promise((resolve) => {
    execFile(process.execPath, [bin, ...args], { env, timeout: 60_000 }, (error, stdout, stderr) => {
      resolve({ code: error ? (typeof error.code === "number" ? error.code : 1) : 0, output: stdout + stderr });
    });
  });
}

/** A fake `xdg-open`/`open`: follows the authorize redirect to the bridge's callback, as an auto-consenting browser would. */
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
