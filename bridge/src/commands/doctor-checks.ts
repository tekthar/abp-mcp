import { findResourceMetadataUrl, parseWwwAuthenticate, type FetchLike } from "../auth/oauth.js";

/** One `doctor` step's outcome. A `warn` result passes, but is flagged. */
export interface StepResult {
  readonly ok: boolean;
  readonly warn?: boolean;
  readonly detail: string;
  readonly fix?: string;
}

/** The server-side guide to MCP OAuth (RFC 9728 metadata and the 401 challenge). */
export const OAUTH_GUIDE_URL = "https://github.com/tekthar/abp-mcp/blob/main/docs/oauth.md";

/** What the "Reach the host" step saw: the status and `WWW-Authenticate` header of an unauthenticated request. */
export interface HostProbe {
  readonly status: number;
  readonly challenge: string | null;
}

/**
 * For a profile without `auth.authority`, the bridge can sign in only if the host names its authorization server:
 * `resource_metadata` in the 401 challenge, else a document at the RFC 9728 well-known locations.
 */
export async function checkResourceMetadata(fetchImpl: FetchLike, mcpUrl: string, probe: HostProbe | undefined): Promise<StepResult> {
  const named = probe?.challenge
    ? parseWwwAuthenticate(probe.challenge).map((challenge) => challenge.params.resource_metadata).find((value) => value)
    : undefined;
  if (named) {
    return { ok: true, detail: `the 401 challenge names ${named}` };
  }

  const found = await findResourceMetadataUrl(fetchImpl, mcpUrl).catch(() => undefined);
  if (found) {
    return { ok: true, detail: `the challenge names none, but ${found} exists` };
  }

  const what = probe?.status === 401
    ? "the 401 challenge has no resource_metadata"
    : `the host answered HTTP ${probe?.status ?? "?"} instead of a 401 challenge`;
  return {
    ok: false,
    detail: `${what}, and no RFC 9728 document is at the well-known locations, so the bridge cannot find the authorization server.`,
    fix: `Set auth.authority (and auth.scope) in the profile, or make the host advertise its metadata: see ${OAUTH_GUIDE_URL}`,
  };
}

/** A tool no caller can use, because it requires a permission or policy the host does not define. */
export interface UndefinedPermission {
  readonly tool: string;
  readonly permission: string;
}

/**
 * The `undefined_permissions` the host's `/mcp/_explain` reports (AbpMcp 0.4.1 and later), or `undefined` when
 * the endpoint is unavailable or does not report them.
 */
export async function readUndefinedPermissions(fetchImpl: FetchLike, mcpUrl: string): Promise<UndefinedPermission[] | undefined> {
  const response = await fetchImpl(`${mcpUrl}/_explain`, { headers: { accept: "application/json" } });
  if (!response.ok) {
    await response.body?.cancel().catch(() => undefined);
    return undefined;
  }

  const body = await response.json().catch(() => undefined) as { undefined_permissions?: unknown } | undefined;
  if (!Array.isArray(body?.undefined_permissions)) {
    return undefined;
  }

  return body.undefined_permissions.flatMap((row: unknown) => {
    const { tool, permission } = (row ?? {}) as { tool?: unknown; permission?: unknown };
    return typeof tool === "string" && typeof permission === "string" ? [{ tool, permission }] : [];
  });
}

/** The `_explain` step: names every tool refused because of an undefined permission. */
export function describeUndefinedPermissions(rows: UndefinedPermission[] | undefined): StepResult {
  if (rows === undefined) {
    return { ok: true, detail: "unavailable (the host does not serve it to this user, or runs AbpMcp before 0.4.1)" };
  }

  if (rows.length === 0) {
    return { ok: true, detail: "no tool requires an undefined permission" };
  }

  return {
    ok: true,
    warn: true,
    detail: `${rows.length} tool(s) refused for every caller, because the host does not define their permission: ` +
      rows.map((row) => `${row.tool} (${row.permission})`).join(", "),
    fix: "Define the permission in the host's PermissionDefinitionProvider, or fix the name in [Authorize].",
  };
}

/** A duration such as "14 min 5 s". */
export function formatRemaining(ms: number): string {
  const seconds = Math.max(0, Math.ceil(ms / 1000));
  const minutes = Math.floor(seconds / 60);
  return minutes > 0 ? `${minutes} min ${seconds % 60} s` : `${seconds} s`;
}

/** Whether the host sets server instructions, which Claude reads with its tools. */
export function describeInstructions(instructions: string | undefined): StepResult {
  const text = instructions?.trim();
  if (!text) {
    return { ok: true, detail: "none: the host sets no server instructions, so Claude sees only the bridge's" };
  }

  const preview = text.length > 80 ? `${text.slice(0, 77).replace(/\s+/g, " ")}...` : text.replace(/\s+/g, " ");
  return { ok: true, detail: `${text.length} characters: "${preview}"` };
}
