import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { NotSignedInError } from "../auth/errors.js";
import { chooseScope } from "../auth/oauth.js";
import { decodeJwtClaims } from "../auth/token-manager.js";
import { ToolFilter } from "../filter.js";
import type { Io } from "../io.js";
import { describeError } from "../log.js";
import type { Runtime } from "../runtime.js";
import { Upstream, type HttpMethods } from "../upstream/upstream.js";

interface StepResult {
  readonly ok: boolean;
  readonly detail: string;
  readonly fix?: string;
}

export interface DoctorOptions {
  /** Also report the filtered tool count and the schema size Claude pays for in every conversation. */
  readonly tools: boolean;
  readonly fetch?: typeof fetch;
}

/**
 * Check a profile step by step, from reaching the host to listing tools, and stop at the first failing step
 * with what to do about it. Never opens a browser and never sends a tool call. Returns false on a failure.
 */
export async function doctor(runtime: Runtime, options: DoctorOptions, io: Io): Promise<boolean> {
  const { profile, tokens } = runtime;
  const fetchImpl = options.fetch ?? globalThis.fetch;
  const signsIn = profile.auth.type === "browser" || profile.auth.type === "password" || profile.auth.type === "client_credentials";
  let tools: Tool[] = [];
  let methods: HttpMethods | undefined;

  const upstream = new Upstream({ profile, tokens, log: runtime.log, version: runtime.version, interactive: false, onToolListChanged: () => undefined });
  const steps: [string, () => Promise<StepResult>][] = [
    ["Profile", async () => ({
      ok: true,
      detail: `${profile.name}: ${profile.url} (${profile.stage}, ${profile.auth.type} sign-in, ${profile.tools.preset} tools)`,
    })],
    ["Reach the host", async () => {
      try {
        const response = await fetchImpl(profile.url, { headers: { accept: "application/json, text/event-stream" }, signal: AbortSignal.timeout(10_000) });
        await response.body?.cancel().catch(() => undefined);
        const challenge = response.headers.get("www-authenticate");
        return { ok: true, detail: `HTTP ${response.status}${challenge ? `, challenge: ${challenge}` : ""}` };
      } catch (error) {
        return { ok: false, detail: describeError(error), fix: reachFix(error) };
      }
    }],
    ...(signsIn ? [
      ["Authorization server", async () => {
        try {
          const target = await tokens.signInTarget();
          const scope = chooseScope(profile.auth.scope, target);
          if (profile.auth.type === "browser" && !target.server.authorizationEndpoint) {
            return { ok: false, detail: `${target.server.issuer} has no authorization endpoint.`, fix: "Use password or client_credentials sign-in, or fix the server metadata." };
          }

          const notes = [
            `issuer ${target.server.issuer}`,
            `token endpoint ${target.server.tokenEndpoint}`,
            target.resource ? `resource ${target.resource}` : undefined,
            scope ? `scope "${scope}"` : "no scope configured or advertised: set auth.scope",
            scope && !scope.split(" ").includes("offline_access") && profile.auth.type !== "client_credentials"
              ? "no offline_access, so no refresh token: add it to auth.scope" : undefined,
          ].filter(Boolean);
          return { ok: true, detail: notes.join("; ") };
        } catch (error) {
          return { ok: false, detail: describeError(error), fix: "Set auth.authority (and auth.scope) in the profile." };
        }
      }] as [string, () => Promise<StepResult>],
      ["Sign-in pause", async () => {
        const status = runtime.breaker.status();
        return status.open
          ? { ok: false, detail: `paused until ${status.until.toISOString()} (${status.reason})`, fix: `Fix the credentials, then run \`abp-mcp login ${profile.name}\`.` }
          : { ok: true, detail: "not paused" };
      }] as [string, () => Promise<StepResult>],
    ] : []),
    ["Sign-in", async () => {
      try {
        const token = await tokens.getAccessToken({ interactive: false });
        if (token === null) {
          return { ok: true, detail: "no sign-in (auth.type none)" };
        }

        const claims = decodeJwtClaims(token);
        const who = claims?.preferred_username ?? claims?.unique_name ?? claims?.name ?? claims?.sub;
        return { ok: true, detail: `${typeof who === "string" ? `signed in as ${who}` : "have a token"}; refresh tokens are kept in ${runtime.vault.refreshTokenStore}` };
      } catch (error) {
        return {
          ok: false,
          detail: describeError(error),
          fix: error instanceof NotSignedInError ? `Run \`abp-mcp login ${profile.name}\`.` : "Check the client id, scopes and credentials.",
        };
      }
    }],
    ["tools/list", async () => {
      try {
        tools = await upstream.listTools();
        const host = upstream.serverInfo;
        return { ok: true, detail: `${tools.length} tool(s) from ${host ? `${host.name} ${host.version}` : "the host"}` };
      } catch (error) {
        return { ok: false, detail: describeError(error), fix: "If the host answers 401, sign in again; if 403, the user lacks access to the MCP endpoint." };
      }
    }],
    ["/mcp/_discover", async () => {
      methods = await upstream.discoverHttpMethods();
      if (methods) {
        return { ok: true, detail: `${methods.size} tool(s) with HTTP methods` };
      }

      return profile.tools.preset === "read-only"
        ? { ok: false, detail: "unavailable, so the read-only preset hides every tool", fix: "Make sure the host maps MapAbpMcp (which serves _discover) and the user can reach it." }
        : { ok: true, detail: "unavailable (only the read-only preset needs it)" };
    }],
  ];

  try {
    for (const [name, run] of steps) {
      const result = await run();
      io.out(`${result.ok ? "✓" : "✗"} ${name}: ${result.detail}`);
      if (!result.ok) {
        if (result.fix) {
          io.out(`  Fix: ${result.fix}`);
        }

        return false;
      }
    }

    if (options.tools) {
      reportTools(tools, methods, new ToolFilter(profile.tools), io);
    }

    return true;
  } finally {
    await upstream.close();
  }
}

function reportTools(tools: Tool[], methods: HttpMethods | undefined, filter: ToolFilter, io: Io): void {
  const visible: Tool[] = [];
  const hidden: string[] = [];
  for (const tool of tools) {
    const decision = filter.decide({ name: tool.name, httpMethod: methods ? (methods.get(tool.name) ?? null) : undefined });
    if (decision.visible) {
      visible.push(tool);
    } else {
      hidden.push(`${tool.name} (${decision.reason})`);
    }
  }

  // About four characters per token for JSON schemas.
  const estimate = (list: Tool[]): number => Math.round(JSON.stringify(list).length / 4);
  io.out(`Tools: ${visible.length} of ${tools.length} shown, about ${estimate(visible)} tokens of tool definitions per conversation (${estimate(tools)} unfiltered).`);
  for (const line of hidden) {
    io.out(`  hidden: ${line}`);
  }
}

function reachFix(error: unknown): string {
  const text = describeError(error);
  if (/ENOTFOUND|EAI_AGAIN/.test(text)) {
    return "Check the host name, and that you are on the network or VPN that reaches it.";
  }

  if (/ECONNREFUSED/.test(text)) {
    return "Nothing is listening at that address: is the application running, and is the port right?";
  }

  if (/CERT|certificate|self.signed/i.test(text)) {
    return "Node does not trust the server's certificate. Point NODE_EXTRA_CA_CERTS at your CA's PEM file.";
  }

  return "Check the URL and your network.";
}
