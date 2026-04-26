# Security Policy

## Supported versions

`abp-mcp` is in pre-alpha. Until v1.0.0, security fixes are applied to the latest release only. There is no LTS branch.

| Version | Supported |
|---------|-----------|
| latest published `0.x` | ✅ |
| any older `0.x` | ❌ — please upgrade |

## Reporting a vulnerability

**Do not open a public GitHub issue for security problems.** That alerts attackers before users can patch.

Use one of these private channels:

1. **Preferred — GitHub private vulnerability reporting:** [Report a vulnerability](https://github.com/tekthar/abp-mcp/security/advisories/new). Tracked privately, lets us coordinate a fix and CVE.
2. **Email backup:** open a draft GitHub Security Advisory (link above). If GitHub is unavailable, email the maintainers via the `tekthar` GitHub org page.

Please include:
- A clear description of the vulnerability and the impact
- Affected versions (if known)
- A minimal reproducer — ideally a small `[McpTool]`-decorated service plus the MCP request that triggers the issue
- Whether you've shared this with anyone else

## What to expect

- **Acknowledgement** within 72 hours of report.
- **Initial assessment** within 7 days (severity, affected versions, fix complexity).
- **Fix timeline** depends on severity:
  - Critical (auth bypass, RCE, data exfiltration past permissions): patch within 14 days.
  - High (information leak, DoS): patch within 30 days.
  - Medium / low: rolled into the next regular release.
- **Credit:** with your permission, we'll credit you in the release notes and the GitHub Security Advisory.

## What counts as a vulnerability

In scope:
- An MCP tool returning data the caller doesn't have ABP permission for
- An exception path that leaks a stack trace, connection string, or other server-internal info to the agent
- A way to invoke a service method that isn't `[McpTool]`-decorated
- A way to bypass `DisabledTools`, `[McpIgnore]`, or the cert-signing requirement
- A dependency vulnerability that's exploitable via abp-mcp's surface

Out of scope (please don't report these as vulnerabilities):
- A misconfigured host that exposes `/mcp` without auth (that's the host's responsibility, see `AbpMcpOptions.AllowAnonymous`)
- Issues in `Volo.Abp.*` or `ModelContextProtocol.*` packages — report those upstream
- Issues that require the attacker to already have host-level code execution

## Coordinated disclosure

We follow a 90-day disclosure window. After a fix ships, the advisory becomes public. If you need a longer window for coordinated downstream patching, tell us in the report.
