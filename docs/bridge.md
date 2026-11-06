# The abp-mcp bridge: setting up an app, and how it works

[The abp-mcp bridge](../bridge/README.md) (npm package `abp-mcp`) is the client-side companion to AbpMcp: a small Node program on each user's
machine that Claude Code or Claude Desktop starts over stdio. It signs the user in to the ABP app, keeps the token
fresh, applies the profile's tool filter, and forwards every call to the app's `/mcp` endpoint as that user.

The server package does not change for it. AbpMcp stays in-process and HTTP-only; the bridge is just another MCP
client of `/mcp`, with the user's own bearer token.

**Hosts should run AbpMcp 0.4.1 or later.** In 0.4.0 and earlier, one tool whose `[Authorize]` names a permission
the host does not define makes `tools/list` fail with `-32603` for every caller, so bridge users see no tools at
all. 0.4.1 hides only that tool and reports it in `/mcp/_explain` (`undefined_permissions`). When `tools/list`
fails with `-32603`, the bridge's error says so, and `abp-mcp doctor` names each tool with an undefined permission.

```text
Claude Code / Claude Desktop
        │  stdio (JSON-RPC)
        ▼
abp-mcp --profile staging
   ├─ sign-in ──────► AuthServer: /connect/authorize, /connect/token   (OpenIddict)
   │      access token in memory; refresh token in the OS keychain
   ├─ tool filter (preset + allow/deny/include; on list AND call)
   └─ MCP client ───► POST https://staging-api.example.com/mcp
                      Authorization: Bearer <the user's access token>
                      (GET /mcp/_discover for the read-only preset)
```

## Setting up the app

### A client for browser sign-in (recommended)

Browser sign-in uses the authorization code flow with PKCE and a loopback redirect. Register a public client that
allows it. OpenIddict matches redirect URIs exactly, ports included, so register every callback port the bridge may
use (`auth.callbackPorts` in the profile, default 33418, 33419 and 33420; the bridge takes the first free one):

```csharp
await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
{
    ClientId = "MyApp_Mcp",
    ClientType = OpenIddictConstants.ClientTypes.Public,
    ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
    DisplayName = "MyApp for Claude (abp-mcp)",
    RedirectUris =
    {
        new Uri("http://127.0.0.1:33418/callback"),
        new Uri("http://127.0.0.1:33419/callback"),
        new Uri("http://127.0.0.1:33420/callback"),
    },
    Permissions =
    {
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.Endpoints.Revocation,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Scopes.Profile,
        OpenIddictConstants.Permissions.Prefixes.Scope + "MyApp",
    },
    Requirements =
    {
        OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
    },
});
```

In ABP solutions this usually goes in the AuthServer's `OpenIddictDataSeedContributor`, next to the other clients.
If your redirect URIs must use `localhost`, set `auth.redirectHost` to `localhost` in the profile. The bridge then
listens on both `127.0.0.1` and `::1`, so a browser that resolves `localhost` to IPv6 still reaches it. RFC 8252
(section 7.3) recommends `127.0.0.1`, which works the same everywhere.

Users then need only the endpoint URL and the client id:

```bash
abp-mcp profiles add staging --url https://staging-api.example.com/mcp --auth browser --client-id MyApp_Mcp \
  --authority https://staging-auth.example.com --scope "openid profile offline_access MyApp"
```

Ask for `offline_access`, so the bridge gets a refresh token and users are not sent back to the browser every hour.

### Password sign-in (deprecated, but supported)

OAuth 2.1 drops the resource-owner password grant, and it does not work for users who sign in with two-factor
authentication or an external provider. It remains useful for apps that have no browser sign-in, so the bridge keeps
it. The client must allow `GrantTypes.Password` (and `GrantTypes.RefreshToken`). Passwords are never written to a
file: they come from `ABP_MCP_PASSWORD`, a prompt in `login`, or the OS keychain with `login --save-password`.

After one rejected password the bridge pauses sign-in for that profile for 15 minutes, so it cannot trip ABP's
account lockout by retrying.

### Token lifetimes

How often users sign in again depends on the AuthServer. With OpenIddict's defaults, refresh tokens last 14 days and
each use returns a new one, so a profile in regular use stays signed in. To change it:

```csharp
PreConfigure<OpenIddictServerBuilder>(builder =>
{
    builder.SetAccessTokenLifetime(TimeSpan.FromHours(1));
    builder.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
});
```

### Letting the bridge discover everything (optional)

When a profile has no `auth.authority`, the bridge sends one unauthenticated request to `/mcp` and reads the 401's
`WWW-Authenticate` header. If it names protected-resource metadata (RFC 9728,
`resource_metadata="https://api.example.com/.well-known/oauth-protected-resource/mcp"`), or that document exists at
the well-known location, the bridge takes the authorization server and scopes from it and sends its `resource` value
(RFC 8707) when signing in. Without it, profiles set `auth.authority` and `auth.scope`. For a profile without
`auth.authority`, `doctor` has a "Protected-resource metadata" step that fails when neither the 401 challenge nor
the well-known locations name the metadata; [docs/oauth.md](oauth.md) shows how to set it up on the host.

`auth.authority` is only for hosts that publish no metadata. When it is set, it chooses the authorization server, but
the bridge still reads the host's metadata, if there is any, for the `resource` and the scopes, and warns when the
authority is not one the host lists. `profiles add` does not ask for an authority when the host publishes metadata.
The bridge never signs in without a scope: with no `auth.scope` and none advertised, `login` and `doctor` stop and
say to set `auth.scope`. Without an API scope, the token has no audience the host accepts. When the host answers 401
to a token the bridge has only just obtained, the bridge reports that the host rejected it (with the host's
`WWW-Authenticate` error) rather than "not signed in", because signing in again would only get the same token.

### Where the refresh token is kept

The bridge keeps refresh tokens in the OS keychain (macOS Keychain, Windows Credential Manager, libsecret) when there
is one, and otherwise in an owner-only file under the bridge's config directory. Windows Credential Manager holds at
most 2560 bytes per secret, which is 1280 characters as stored, and ABP's OpenIddict refresh tokens are longer than
that. When the keychain refuses a token, the bridge keeps it in the file instead, removes any older keychain entry,
and says so once. On Linux the keychain means the Secret Service (gnome-keyring, KWallet, KeePassXC) only: the
library's fallback, the kernel keyring, is cleared at every reboot, so a machine without a Secret Service (a server,
WSL) uses the file. `login` and `doctor` name the store the profile's token is actually in. `ABP_MCP_KEYCHAIN=off`
always uses the file.

Every discovered URL follows the same rule as the URLs in a profile: https, or plain http only for `localhost`,
`127.0.0.1` and `[::1]`. That covers the `resource_metadata` URL, the authorization server, and its token,
authorization and revocation endpoints, whether or not `auth.authority` is set. The bridge also checks that the
metadata's `resource` is the profile's URL (or a parent path on the same origin) and that the authorization server's
`issuer` is the one it was fetched for, and it refuses to sign in otherwise. The browser is opened only for an https
(or loopback http) URL. No other scheme is ever passed to the OS URL handler.

[MCP OAuth for ABP + OpenIddict](oauth.md) shows how to publish that metadata, register the resource with OpenIddict
and check the result.

### The read-only preset needs `/mcp/_discover`

Production profiles default to the `read-only` preset, which shows only tools backed by GET endpoints. The bridge
reads each tool's HTTP method from `/mcp/_discover`, which `MapAbpMcp` serves. If `_discover` is unavailable to the
user, the preset hides every tool rather than guess.

## Gateway mode and `run_code`

`tools.gateway` keeps a large tool set out of Claude's context ({I(120)}, {I(121)}). The bridge lists only the
`pinned` tools (globs) and four of its own: `search_tools`, `describe_tools`, `call_tool` and, unless `code` is
`false`, `run_code`. The tools it searches are the ones the profile's filter allows, from the last `tools/list`; the
host's `tools/list_changed` refreshes them.

- **Search** is keyword ranking with no embeddings: names are split on camelCase, snake_case and dots, a name match
  outweighs a description match, a word matches its plural, and a result must contain every word of a one- or
  two-word query (60% of a longer one). Results carry a one-line description and, when the bridge has read the host's
  `_discover`, the HTTP method.
- **`call_tool`** takes the same path as a call to a listed tool: the filter (a write tool under the read-only preset
  is refused locally), the audit log, and the retry rules below.
- **`run_code`** runs an async JavaScript function in QuickJS compiled to WebAssembly, a fresh runtime per run with
  no network, file, process or module access. The function reaches the host only through `tools.<name>(args)` (or
  `tools.call(name, args)`), `tools.search` and `tools.describe`, and every call takes the `call_tool` path. A call
  resolves to the tool's structured result, or its text parsed as JSON, and throws when the tool reports an error.
  Limits: 60 seconds for the whole run (an interrupt also stops CPU loops), 50 tool calls, 64 MB of memory, 20,000
  characters of code. The result is cut to about 24,000 characters, largest values first, and stays valid JSON.
- **The call journal**: the answer always lists the run's tool calls as `ok`, `error`, `refused` (stopped by the
  bridge, never sent) or `unknown` (it may have run: the connection broke after it was sent, or the run ended while it
  was in flight). When a script fails part-way, Claude can see which writes already happened instead of repeating them.

An AbpMcp host can also keep tools out of `tools/list` itself ({I(119)}): its `mcp_search_tools`,
`mcp_describe_tools` and `mcp_call_tool(s)` are ordinary tools to the bridge, which can pin them or find them by
search.

## How the bridge decides to retry

A request is sent again only when the app refused it before running anything:

- **401**: the token expired or was revoked. The bridge renews it (refresh token, then the profile's credentials) and
  sends the request once more.
- **404 with a session id**: the app ended the MCP session (idle timeout, restart). The bridge opens a new session and
  sends the request once more.
- **The session never opened**: opening the MCP session (`initialize`) failed before a `tools/call` was sent. The
  call never left the machine, so the bridge tries once more on a fresh session; if that fails too, Claude gets
  `BRIDGE_UNREACHABLE` ("nothing was sent").

Anything else after a `tools/call` was sent (a dropped connection, a broken response stream, a timeout, a 5xx) may
mean the call ran, so the bridge returns `BRIDGE_CALL_OUTCOME_UNKNOWN` and never re-sends it. `tools/list` and
`initialize` change nothing, so they are retried once on a fresh session after any transport failure.

When several requests fail on the same expired session, the bridge opens one new session and they all use it; a
session that was replaced is closed only after the requests still running on it have settled.

## Startup: the app's instructions and `initialize`

Claude reads the server's name and instructions only from `initialize`, once per session. The bridge therefore holds
its answer to Claude's `initialize` until the session with the app is open, for up to 20 seconds, so Claude sees the
app's own `serverInfo` and `instructions` (followed by the profile's stage and `notes`). While the app is unreachable,
the bridge tries again every half second within that window.

- **Browser profile, first run or expired sign-in**: `initialize` starts the browser sign-in (Claude lists the tools
  right after `initialize`, which would start it anyway). If the user finishes within the window, `initialize` carries
  the app's instructions. The sign-in is shared with the first `tools/list`, so the browser opens once.
- **Fallback**: if the session is not open within the window (the app is down, or the user is still in the browser),
  `initialize` answers with the bridge's own name (`abp-mcp`) and instructions, which say that the app's instructions
  are missing from this session. The bridge keeps working: the sign-in finishes in the background and the tool list
  updates by itself. To get the app's instructions, restart the MCP server once the app is reachable (in Claude Code,
  `/mcp`; in Claude Desktop, restart the app).
- A sign-in problem the user must fix (no saved credential, paused sign-in) answers at once, with the fallback.

## Several bridges on one profile

Claude Code and Claude Desktop often run the same profile at the same time, sharing its stored refresh token. With
refresh-token rotation (OpenIddict's default) one bridge can renew with a token the other has just used. When the
authorization server rejects a refresh token, the bridge reads the stored one again and, if another bridge stored a
newer one, renews with that once. It deletes the stored refresh token only if it is still the one that was rejected.

## The audit log

`ABP_MCP_AUDIT=1` is best-effort. A failed write (an unwritable directory, a full disk) never changes the result of
the tool call it describes: the bridge logs one warning and pauses auditing for a minute after each failure. If another
process holds the file open during rotation (`EPERM`/`EBUSY` on Windows), the bridge skips the rotation and keeps
appending to the current file.

## What `doctor` checks

`abp-mcp doctor <profile>` runs these steps in order and stops at the first failure, with a fix:

1. **Profile**: what the profile resolves to.
2. **Reach the host**: an unauthenticated request to the endpoint, and the `WWW-Authenticate` challenge it gets.
3. **Protected-resource metadata** (only without `auth.authority`): the challenge's `resource_metadata`, or a
   document at the RFC 9728 well-known locations.
4. **Authorization server** and **Sign-in pause** (profiles that sign in): the server's endpoints and scopes, and
   whether sign-in is paused after a rejected credential, with the time left.
5. **Sign-in**: a token without opening a browser.
6. **tools/list**: the tools the host lists for this user.
7. **Host instructions**: whether the host sets server instructions for Claude.
8. **/mcp/_explain**: tools no caller can use because they require a permission the host does not define. These are
   flagged with `!`, not treated as a failure.
9. **/mcp/_discover**: each tool's HTTP method, which the read-only preset needs.

## Releasing the bridge

The bridge is versioned separately from the NuGet package.

1. Update `bridge/package.json`'s `version` and add a `## [X.Y.Z]` section to `bridge/CHANGELOG.md`.
2. Merge, then push a tag `bridge-vX.Y.Z`. (The NuGet release workflow takes `v*` tags, so the two never collide.)
3. `.github/workflows/release-bridge.yml` tests the package, then, in this order:
   1. builds the Claude Desktop extension and checks it (manifest, version, the bundled bridge runs);
   2. packs the npm tarball and checks its contents;
   3. creates the GitHub Release as a **draft**, with the `.mcpb` and the tarball attached and the changelog section
      as notes;
   4. publishes that tarball to npm with provenance, unless npm already has this version;
   5. publishes the release.

   npm comes last, so a failed build never leaves a version on npm without its release, and re-running a failed
   workflow skips a publish that already happened. A pre-release version (`1.2.3-rc.1`) goes to the `next` dist-tag.

npm publishing uses trusted publishing (OIDC, no token secret): on npmjs.com, the `abp-mcp` package has a trusted
publisher with repository `tekthar/abp-mcp` and workflow `release-bridge.yml`. The workflow has no `NPM_TOKEN`; if
the trusted publisher is missing, the publish step fails and the release stays a draft.

## Developing

```bash
cd bridge
npm ci
npm run lint && npm run typecheck
npm test                                                     # unit + integration (stub app and AuthServer)
dotnet run --project ../samples/AbpMcp.Sample &              # then:
ABP_MCP_E2E_URL=http://localhost:5000/mcp npm run test:e2e   # the built bridge over stdio, against the sample
npm run pack:mcpb                                            # the Claude Desktop extension
```

The authenticated end-to-end tests (`test/e2e/authenticated.test.ts`) run the same sample with sign-in turned on, set
up as [MCP OAuth for ABP + OpenIddict](oauth.md) describes, against a stub authorization server the test starts on the
issuer port. They cover browser sign-in and silent renewal, a user without a permission, `doctor`, and discovery
against the metadata `AddMcp` serves:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5002 \
Sample__Auth__Issuer=http://127.0.0.1:5001 \
Sample__Auth__Resource=http://127.0.0.1:5002/mcp \
Sample__Auth__Grants="3a1f0c9e-0d4b-4c5e-9b7a-2f6d8e1c4a01:Library.Members.Delete" \
dotnet run --project ../samples/AbpMcp.Sample &
ABP_MCP_E2E_AUTH_URL=http://127.0.0.1:5002/mcp ABP_MCP_E2E_AUTH_ISSUER_PORT=5001 npm run test:e2e
```

The `Bridge` workflow runs all of it on every pull request that touches `bridge/`, `src/` or `samples/`.
