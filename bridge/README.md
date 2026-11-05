# abp-mcp-bridge

**Connect Claude Code and Claude Desktop to any ABP app that uses [AbpMcp](https://github.com/tekthar/abp-mcp), signed in as you.**

The bridge runs on your machine. Claude starts it and talks to it over stdio; it signs you in to the app, keeps your
token fresh, optionally trims the tool list, and forwards every call to the app's `/mcp` endpoint as you. The app's
own permissions decide what you can do: the bridge can hide tools, never unlock them.

```bash
npx -y @tekthar/abp-mcp-bridge profiles add staging --url https://staging-api.example.com/mcp \
  --stage staging --auth browser --client-id MyApp_Mcp
npx -y @tekthar/abp-mcp-bridge login staging
npx -y @tekthar/abp-mcp-bridge install staging --claude-code
```

## Why a bridge?

| | Claude connector in the cloud | `claude mcp add --transport http` | **abp-mcp-bridge** |
|---|---|---|---|
| Reaches intranet and VPN-only apps | no | yes | **yes** |
| Needs MCP-specific OAuth set up on the app | yes | yes, or a pasted token | **no**: uses the app's existing OpenIddict clients |
| Token renewal | automatic | automatic, or by hand with a pasted token | **automatic** |
| Trims the tool list (read-only, globs) | no | no | **yes** |
| Several environments, clearly labelled | no | one entry each, by hand | **profiles** |

## Install

You need Node.js 22 or later. Nothing else: `npx` fetches the bridge on first use.

**Claude Code**: add a profile, sign in, then `install <profile> --claude-code` (runs `claude mcp add` for you;
`--scope user|project|local`, default `user`).

**Claude Desktop**: download `abp-mcp-bridge-<version>.mcpb` from the
[releases](https://github.com/tekthar/abp-mcp/releases) and open it. Claude Desktop asks for the app's MCP URL, the
sign-in type and client id, and keeps any password in your OS keychain. Or, with a profile set up, run
`install <profile> --claude-desktop`, which adds it to `claude_desktop_config.json` after backing the file up.

## Profiles

One profile per environment, in `~/.config/abp-mcp/profiles.json` (`%APPDATA%\abp-mcp\profiles.json` on Windows;
`ABP_MCP_PROFILES` overrides the path). No secret is ever stored in it.

```json
{
  "profiles": {
    "staging": {
      "url": "https://staging-api.example.com/mcp",
      "stage": "staging",
      "auth": { "type": "browser", "clientId": "MyApp_Mcp" },
      "tools": { "deny": ["*_Delete"] },
      "notes": "Shared staging. Confirm with the user before writes."
    },
    "prod": {
      "url": "https://api.example.com/mcp",
      "stage": "production",
      "auth": {
        "type": "password",
        "clientId": "MyApp_App",
        "authority": "https://auth.example.com",
        "scope": "openid profile offline_access MyApp",
        "username": "alice"
      },
      "tools": { "include": ["*_List"] }
    }
  }
}
```

| Key | Meaning |
|---|---|
| `url` | The app's AbpMcp endpoint. `https` only, except `localhost`. |
| `stage` | `development` (default), `staging` or `production`. Claude is told which one it is talking to, and **production is read-only** unless `tools.preset` is `all`. |
| `auth` | How to sign in (below). |
| `tools` | `preset` (`all` or `read-only`), and `allow`, `deny` and `include` glob lists (below). |
| `notes` | Extra guidance for Claude, added to the server instructions. |
| `timeoutSeconds` | How long a tool call may run (default 300). |

## Signing in

| `auth.type` | How | When |
|---|---|---|
| `browser` | Authorization code + PKCE in your browser, with a loopback redirect (`http://127.0.0.1:<port>/callback`, ports `callbackPorts`, default 33418–33420). | The default. Needs a client that allows the code flow and those redirect URIs. |
| `password` | The resource-owner password grant. **Deprecated** in OAuth 2.1; kept for apps that have no browser sign-in. | The app's client allows the password grant. |
| `client_credentials` | A confidential client. | Service accounts, CI. |
| `token` | A bearer token from `ABP_MCP_TOKEN` (or `auth.tokenEnv`, `auth.tokenFile`). | Tests, emergencies. |
| `none` | No `Authorization` header. | A local app that allows anonymous access. |

`clientId` is required for the first three; `username` for `password`. When `authority` is not set, the bridge
discovers the authorization server from the app (RFC 9728 metadata named in its 401 challenge, then OpenID Connect or
RFC 8414 discovery) and sends the app's `resource` indicator (RFC 8707). `scope` defaults to what the app advertises,
plus `offline_access`.

**Where secrets live.** The refresh token goes in your OS keychain (macOS Keychain, Windows Credential Manager,
libsecret), or, on a machine without one, in an owner-only file. A password or client secret is never written to a
file: it comes from `ABP_MCP_PASSWORD` / `ABP_MCP_CLIENT_SECRET`, from a prompt in `login`, or from the OS keychain if
you ran `login --save-password` (`--save-secret`).

**How long a sign-in lasts.** The bridge renews the access token a minute before it expires, using the refresh token,
and stores the new refresh token the server returns each time. With OpenIddict's defaults (refresh tokens valid for 14
days and replaced on every use), a profile you use at least every two weeks never asks you to sign in again; check your
app's settings. When the refresh token does expire or is revoked:

- a `browser` profile opens the browser (one click if you are still signed in to the app);
- a `password` profile signs in again silently with the saved or environment password, or asks you to run `login`.

**Lockout safety.** If the server rejects a password or client secret, the bridge stops trying for that profile for 15
minutes (across restarts), so one wrong password cannot lock the account under ABP's lockout policy. `login` clears it.

## Tool filters

The filter applies to `tools/list` and again to every `tools/call`; a hidden tool is refused locally and never sent.

- `preset: "read-only"` shows tools backed by GET endpoints, using the app's `/mcp/_discover`. If `_discover` is
  unavailable it shows nothing rather than guess. ABP maps methods by name, so `GetListAsync` is a GET but a method
  named `ListAsync` is a POST: add such read-only tools with `include`.
- `allow`: when present, only matching tools are shown.
- `deny`: matching tools are hidden. Deny wins over everything.
- `include`: tools to show although the preset hides them.

Globs use `*` and `?` and ignore case. The filter is a guardrail for the agent, not a security boundary: your
permissions in the app are what protect its data, through the bridge or not.

## What the bridge guarantees

- **It never widens access.** Every request carries your token; the app's `[Authorize]`, `[McpTool]` opt-in and kill
  switch still decide everything.
- **It never replays a call.** A request is sent again only when the app refused it before running anything: a 401
  (after renewing the token) or a 404 for an expired session (after reconnecting). If the connection fails after a
  `tools/call` was sent, Claude gets `BRIDGE_CALL_OUTCOME_UNKNOWN` ("it may have run") instead of a retry.
- **It never logs secrets.** Tokens, passwords, codes and client secrets are masked in every log line, debug included.

The app's own errors (`[FORBIDDEN] ...`, `[VALIDATION_ERROR] ...`, your business codes) reach Claude unchanged. The
bridge's own start with `BRIDGE_`:

| Code | Meaning |
|---|---|
| `BRIDGE_TOOL_HIDDEN` | The profile's filter hides this tool; it was not sent. |
| `BRIDGE_CALL_OUTCOME_UNKNOWN` | The connection failed after the call was sent; check the app before retrying. |
| `BRIDGE_UNREACHABLE` | The app could not be reached; nothing was sent. |
| `BRIDGE_SIGN_IN_REQUIRED` | Not signed in, or sign-in is paused; the message says what to run. |
| `BRIDGE_UNAUTHORIZED` | The app rejected the sign-in (401/403). |

## Commands

| Command | Does |
|---|---|
| `abp-mcp-bridge --profile <name>` | Serves the profile over stdio. This is what Claude runs. |
| `login <profile> [--save-password \| --save-secret]` | Signs in now and keeps the sign-in. |
| `logout <profile>` | Revokes and forgets the stored sign-in, saved password and any sign-in pause. |
| `profiles list \| add <name> \| remove <name>` | Edits `profiles.json`. `add` asks for what its flags don't give. |
| `doctor <profile> [--tools]` | Checks each step (reach the app, authorization server, sign-in, `tools/list`, `_discover`) and stops at the first failure with the fix. `--tools` adds the filtered tool count and an estimate of the context it costs. |
| `install <profile> --claude-code [--scope user\|project\|local] [--name <name>]` | Registers the profile with Claude Code. |
| `install <profile> --claude-desktop [--name <name>]` | Adds the profile to Claude Desktop's config. |

## Environment variables

| Variable | Use |
|---|---|
| `ABP_MCP_PROFILES` | Path of the profiles file. |
| `ABP_MCP_PASSWORD`, `ABP_MCP_CLIENT_SECRET`, `ABP_MCP_TOKEN` | Credentials, never written anywhere. |
| `ABP_MCP_URL`, `ABP_MCP_AUTH`, `ABP_MCP_CLIENT_ID`, `ABP_MCP_AUTHORITY`, `ABP_MCP_SCOPE`, `ABP_MCP_USERNAME`, `ABP_MCP_STAGE`, `ABP_MCP_TOOLS_PRESET`, `ABP_MCP_NOTES` | Define the profile without a profiles file (the Claude Desktop extension does this). |
| `ABP_MCP_DEBUG=1`, `ABP_MCP_LOG_FILE` | Debug logging to stderr, and optionally a file. |
| `ABP_MCP_AUDIT=1` | One JSON line per tool call (tool, outcome, duration, error code; never arguments or results) in the bridge's state directory. |
| `ABP_MCP_KEYCHAIN=off` | Don't use the OS keychain. |

## Setting up the app

The app needs an OpenIddict client the bridge can use. See
[docs/bridge.md](https://github.com/tekthar/abp-mcp/blob/main/docs/bridge.md) for the client settings, redirect URIs,
token lifetimes, and how to let the bridge discover everything from the app.

## License

MIT. Part of [abp-mcp](https://github.com/tekthar/abp-mcp).
