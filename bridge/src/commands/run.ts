import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { AuditLog } from "../audit.js";
import { Bridge } from "../bridge.js";
import type { Runtime } from "../runtime.js";
import { Upstream } from "../upstream/upstream.js";

/**
 * Serve the profile over stdio: what Claude starts. Claude's `initialize` waits (bounded) for the host session,
 * so it carries the host's own `serverInfo` and instructions; see {@link Bridge}.
 */
export async function runStdio(runtime: Runtime): Promise<void> {
  const { profile, tokens, log } = runtime;
  // The bridge needs the upstream, and the upstream reports tool changes to the bridge.
  const late: { bridge?: Bridge } = {};
  const upstream = new Upstream({
    profile,
    tokens,
    log,
    version: runtime.version,
    interactive: profile.auth.type === "browser",
    onToolListChanged: () => late.bridge?.onUpstreamToolsChanged(),
  });

  const audit = AuditLog.fromEnv(runtime.stateDir, profile.name, runtime.env, log);
  const bridge = new Bridge({ profile, version: runtime.version, tokens, upstream, log, ...(audit ? { audit } : {}) });
  late.bridge = bridge;

  const shutdown = (): void => {
    void upstream.close().finally(() => process.exit(0));
  };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
  process.stdin.once("close", shutdown);

  await bridge.start(new StdioServerTransport());
  log.info(`Serving profile '${profile.name}' (${profile.stage}) for ${profile.url}.`);
}
