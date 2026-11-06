import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { icaclsArgs, parseWhoamiSid, restrictToCurrentWindowsUser, windowsAclIdentity } from "../../src/auth/windows-acl.js";

describe("Windows ACL for the credentials file (#105)", () => {
  it("reads the SID from `whoami /user /fo csv`", () => {
    const output = "\"User Name\",\"SID\"\r\n\"azuread\\alicesmith\",\"S-1-12-1-1234567890-1234567890-1234567890-1234567890\"\r\n";
    assert.equal(parseWhoamiSid(output), "S-1-12-1-1234567890-1234567890-1234567890-1234567890");
    assert.equal(parseWhoamiSid("\"CONTOSO\\bob\",\"S-1-5-21-1-2-3-1001\""), "S-1-5-21-1-2-3-1001");
    assert.equal(parseWhoamiSid(""), undefined);
    assert.equal(parseWhoamiSid("\"User Name\",\"SID\""), undefined);
  });

  it("grants by SID, else DOMAIN\\user, else the bare user name", () => {
    assert.equal(windowsAclIdentity("S-1-5-21-1-2-3-1001", { USERDOMAIN: "CONTOSO", USERNAME: "bob" }, "bob"), "*S-1-5-21-1-2-3-1001");
    assert.equal(windowsAclIdentity(undefined, { USERDOMAIN: "AzureAD", USERNAME: "AliceSmith" }, "alice"), "AzureAD\\AliceSmith");
    assert.equal(windowsAclIdentity("not-a-sid", {}, "alice"), "alice");
  });

  it("removes inheritance and grants only that identity full control", () => {
    assert.deepEqual(icaclsArgs("C:\\Users\\a\\AppData\\Roaming\\abp-mcp\\state\\credentials\\p.json.1.tmp", "*S-1-5-21-1-2-3-1001"),
      ["C:\\Users\\a\\AppData\\Roaming\\abp-mcp\\state\\credentials\\p.json.1.tmp", "/inheritance:r", "/grant:r", "*S-1-5-21-1-2-3-1001:F"]);
  });

  it("runs whoami, then icacls with the SID", () => {
    const calls: [string, readonly string[]][] = [];
    restrictToCurrentWindowsUser("p.json", (command, args) => {
      calls.push([command, args]);
      return command === "whoami" ? "\"User Name\",\"SID\"\n\"azuread\\a\",\"S-1-12-1-9-8-7-6\"\n" : "";
    }, {});
    assert.deepEqual(calls, [
      ["whoami", ["/user", "/fo", "csv"]],
      ["icacls", ["p.json", "/inheritance:r", "/grant:r", "*S-1-12-1-9-8-7-6:F"]],
    ]);
  });

  it("falls back to DOMAIN\\user when whoami fails, and lets an icacls failure through", () => {
    const calls: [string, readonly string[]][] = [];
    assert.throws(() => restrictToCurrentWindowsUser("p.json", (command, args) => {
      calls.push([command, args]);
      throw new Error(`${command} failed`);
    }, { USERDOMAIN: "CONTOSO", USERNAME: "bob" }), /icacls failed/);
    assert.deepEqual(calls[1], ["icacls", ["p.json", "/inheritance:r", "/grant:r", "CONTOSO\\bob:F"]]);
  });
});
