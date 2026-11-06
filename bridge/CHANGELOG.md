# Changelog

All notable changes to the bridge, the `abp-mcp` npm package, are documented here. It is versioned separately
from the `AbpMcp` NuGet package, with tags `bridge-vX.Y.Z`.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- **Discovered OAuth URLs now follow the same https rule as the profile's URLs**
  ([GHSA-834p-c9gj-q699](https://github.com/tekthar/abp-mcp/security/advisories/GHSA-834p-c9gj-q699)). Before, only a
  profile's `url` and `auth.authority` were checked. The `resource_metadata` URL from the host's 401 challenge,
  `authorization_servers[0]`, and the `token_endpoint`, `authorization_endpoint` and `revocation_endpoint` from the
  authorization server's metadata were used as given. A malicious or compromised host could have them receive a
  password, refresh token or client secret over plain http, or have a `file:` or custom-scheme URL passed to the OS
  URL handler. Each of these URLs must now be https, or http on a loopback host, and the bridge refuses anything else
  with an error naming the field. In addition:
  - The browser opener refuses every URL that is not https (or http on loopback) before it reaches the OS handler.
  - The protected-resource metadata's `resource` must match the profile's URL (same origin, same path or a parent
    path; RFC 9728 section 3.3), and a document without `resource` is refused.
  - The authorization server metadata's `issuer` must match the issuer it was fetched for (RFC 8414 section 3.3).
  - Token and revocation requests no longer follow redirects, so credentials cannot be redirected elsewhere.

## [0.2.0] — 2026-11-06

### Changed

- **Renamed to `abp-mcp`.** The package is now published unscoped as `abp-mcp`, and the command is `abp-mcp` instead
  of `abp-mcp-bridge`: `npx -y abp-mcp --profile <name>`. `@tekthar/abp-mcp-bridge` 0.1.0 is deprecated. Run
  `install --claude-code` or `install --claude-desktop` again to point an existing registration at the new name.
  Profiles, stored sign-ins and settings carry over unchanged.

## [0.1.0] — 2026-11-06

The first release ([#82](https://github.com/tekthar/abp-mcp/issues/82)), published as `@tekthar/abp-mcp-bridge`.

### Added

- **Stdio-to-HTTP proxy**: Claude Code and Claude Desktop start the bridge, which forwards `initialize`, `tools/list`
  and `tools/call` to an AbpMcp endpoint and passes `notifications/tools/list_changed` through. Claude is told the
  profile's stage, and production profiles ask it to confirm every write.
- **Profiles**: one per environment in `profiles.json`, validated with errors that name the offending key, or defined
  through `ABP_MCP_*` environment variables.
- **Sign-in**: browser (authorization code + PKCE, loopback redirect on a list of ports), password (deprecated in
  OAuth 2.1, kept for apps without browser sign-in), client credentials, static token, or none. Discovery through
  RFC 9728, OpenID Connect and RFC 8414, and the RFC 8707 `resource` parameter.
- **Token handling**: renewal a minute before expiry, rolling refresh tokens stored again on each use, one retry
  after a 401, and a 15-minute sign-in pause after rejected credentials so a wrong password cannot lock the account.
- **Credential storage**: refresh tokens in the OS keychain, or an owner-only file where there is none. Passwords and
  client secrets only in the OS keychain, and only with `login --save-password` / `--save-secret`.
- **Tool filters**: `read-only` preset by HTTP method from `/mcp/_discover` (default for production; fails closed),
  `allow`, `deny` and `include` globs, enforced on list and on call.
- **No replay**: a call is sent again only when the app refused it before running anything (401, or 404 for an
  expired session). A call whose connection failed after it was sent returns `BRIDGE_CALL_OUTCOME_UNKNOWN`.
- **Commands**: `login`, `logout`, `profiles list|add|remove`, `doctor [--tools]`, `install --claude-code` and
  `install --claude-desktop`.
- **Claude Desktop extension** (`.mcpb`) with keychain binaries for Windows, macOS and Linux.
- **Redacted logging** (`ABP_MCP_DEBUG`, `ABP_MCP_LOG_FILE`) and an optional audit log (`ABP_MCP_AUDIT`).
