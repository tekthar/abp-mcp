# Changelog

All notable changes to `@tekthar/abp-mcp-bridge` are documented here. The bridge is versioned separately from the
`AbpMcp` NuGet package, with tags `bridge-vX.Y.Z`.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0]

The first release ([#82](https://github.com/tekthar/abp-mcp/issues/82)).

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
