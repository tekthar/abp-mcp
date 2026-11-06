# Changelog

All notable changes to the bridge, the `abp-mcp` npm package, are documented here. It is versioned separately
from the `AbpMcp` NuGet package, with tags `bridge-vX.Y.Z`.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] — 2026-11-06

A security release: upgrade from 0.2.0 or earlier (and from the deprecated `@tekthar/abp-mcp-bridge`). The version
jumps from 0.2.0 to 0.5.0 to line up with the `AbpMcp` server release it ships alongside; there are no 0.3 or 0.4
bridge releases.

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

### Added

- `doctor`: a "Protected-resource metadata" step for profiles without `auth.authority`, failing when neither the 401
  challenge nor the RFC 9728 well-known locations name the metadata (see docs/oauth.md) ([#108](https://github.com/tekthar/abp-mcp/issues/108)).
- `doctor`: names tools refused for every caller because they require an undefined permission, from `/mcp/_explain`
  (AbpMcp 0.4.1+) ([#108](https://github.com/tekthar/abp-mcp/issues/108)).
- `doctor`: shows whether the host sets server instructions ([#104](https://github.com/tekthar/abp-mcp/issues/104)), and an open sign-in pause shows the time left
  ([#107](https://github.com/tekthar/abp-mcp/issues/107)).
- A `tools/list` that fails with `-32603` now hints that the host should run AbpMcp 0.4.1 or later ([#108](https://github.com/tekthar/abp-mcp/issues/108)).

### Changed

- The app's `serverInfo` and instructions now reach Claude when the bridge could not connect before starting:
  `initialize` waits up to 20 s for the app session, including a first browser sign-in. On timeout it answers with the
  bridge's own info and says the app's instructions are missing ([#104](https://github.com/tekthar/abp-mcp/issues/104)).
- A `tools/call` whose MCP session could not be opened is retried once on a fresh session (it was never sent). A call
  that may have reached the host is still never re-sent ([#108](https://github.com/tekthar/abp-mcp/issues/108)).
- With `auth.redirectHost: localhost`, the sign-in callback also listens on `::1`, for browsers that resolve
  `localhost` to IPv6 ([#105](https://github.com/tekthar/abp-mcp/issues/105)).
- Passwords and client secrets are masked in logs whatever their length; values under six characters wherever they
  stand on their own ([#105](https://github.com/tekthar/abp-mcp/issues/105)).
- Release: the `.mcpb`, the npm tarball and a draft GitHub Release are built and checked before `npm publish`, which
  now uses trusted publishing (no token) and is skipped when the version already exists ([#106](https://github.com/tekthar/abp-mcp/issues/106)).

### Fixed

- A failed audit write (`ABP_MCP_AUDIT=1`) no longer turns a tool call that ran on the app into an error. Auditing is
  best-effort: one warning, a one-minute pause after each failure, and a rotation blocked by another process
  (`EPERM`/`EBUSY`) is skipped while appending continues ([#102](https://github.com/tekthar/abp-mcp/issues/102)).
- Two bridges on one profile (Claude Code and Claude Desktop) no longer delete each other's refresh token: after
  `invalid_grant` the bridge renews once with a newer stored token, and only deletes the token that was rejected
  ([#103](https://github.com/tekthar/abp-mcp/issues/103)).
- When several requests fail on the same expired session, the bridge opens one new session instead of closing the one
  another request just opened, and closes a replaced session only after its requests settle, which avoids spurious
  `BRIDGE_CALL_OUTCOME_UNKNOWN` ([#103](https://github.com/tekthar/abp-mcp/issues/103)).
- A `tools/call` that arrives during a background sign-in check now opens the browser instead of failing with "not
  signed in" ([#103](https://github.com/tekthar/abp-mcp/issues/103)).
- `install --claude-code --scope` accepts only `user`, `project` or `local`; other values never reach the command line
  ([#105](https://github.com/tekthar/abp-mcp/issues/105)).
- Windows: the file-stored refresh token is restricted to the current user by SID, so Entra ID and domain accounts
  work ([#105](https://github.com/tekthar/abp-mcp/issues/105)).

### Documentation

- Hosts should run AbpMcp 0.4.1 or later ([#108](https://github.com/tekthar/abp-mcp/issues/108)). New sections in docs/bridge.md: startup, several bridges on one
  profile, the audit log, and what `doctor` checks.
- Tests for the remaining #82 acceptance criteria: `install`, debug logging through the real CLI, `doctor` with an open
  breaker, `paths.ts`, `auth.tokenFile`, and no second retry after a second 401 ([#107](https://github.com/tekthar/abp-mcp/issues/107)). The authenticated
  end-to-end tier against a real ABP host is still open.

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
