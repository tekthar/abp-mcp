import { execFileSync } from "node:child_process";
import os from "node:os";

/** Runs a command and returns its stdout; injectable so the Windows logic can be tested anywhere. */
export type RunCommand = (command: string, args: readonly string[]) => string;

const SID = /^S-1-\d+(?:-\d+)+$/;

/**
 * The SID in the output of `whoami /user /fo csv`: a header row, then `"DOMAIN\user","S-1-5-..."`.
 * Undefined when the output has no SID (an unexpected format, or `whoami` failed).
 */
export function parseWhoamiSid(output: string): string | undefined {
  const rows = output.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0);
  for (const row of rows.reverse()) {
    const fields = [...row.matchAll(/"((?:[^"]|"")*)"/g)].map((match) => match[1]!.replace(/""/g, "\""));
    const sid = fields.find((field) => SID.test(field));
    if (sid) {
      return sid;
    }
  }

  return undefined;
}

/**
 * Who `icacls` should grant the file to. A SID (`*S-1-...`) always resolves, including for Entra ID
 * (`AzureAD\user`) and domain accounts whose bare user name does not. Without one, `DOMAIN\user` from the
 * environment, and only then the bare user name.
 */
export function windowsAclIdentity(sid: string | undefined, env: NodeJS.ProcessEnv, username: string): string {
  if (sid && SID.test(sid)) {
    return `*${sid}`;
  }

  if (env.USERDOMAIN && env.USERNAME) {
    return `${env.USERDOMAIN}\\${env.USERNAME}`;
  }

  return username;
}

/** The `icacls` arguments that remove inherited entries and grant only `identity` full control. */
export function icaclsArgs(file: string, identity: string): string[] {
  return [file, "/inheritance:r", "/grant:r", `${identity}:F`];
}

const defaultRun: RunCommand = (command, args) =>
  execFileSync(command, args, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true });

/** Restrict a file to the current Windows user. Throws when `icacls` fails. */
export function restrictToCurrentWindowsUser(file: string, run: RunCommand = defaultRun, env: NodeJS.ProcessEnv = process.env): void {
  let sid: string | undefined;
  try {
    sid = parseWhoamiSid(run("whoami", ["/user", "/fo", "csv"]));
  } catch {
    sid = undefined;
  }

  run("icacls", icaclsArgs(file, windowsAclIdentity(sid, env, os.userInfo().username)));
}
