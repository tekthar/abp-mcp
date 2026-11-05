# abp-mcp-bridge: setting up an app, and how it works

[abp-mcp-bridge](../bridge/README.md) is the client-side companion to AbpMcp: a small Node program on each user's
machine that Claude Code or Claude Desktop starts over stdio. It signs the user in to the ABP app, keeps the token
fresh, applies the profile's tool filter, and forwards every call to the app's `/mcp` endpoint as that user.

The server package does not change for it. AbpMcp stays in-process and HTTP-only; the bridge is just another MCP
client of `/mcp`, with the user's own bearer token.

```text
Claude Code / Claude Desktop
        │  stdio (JSON-RPC)
        ▼
abp-mcp-bridge --profile staging
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
    DisplayName = "MyApp for Claude (abp-mcp-bridge)",
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
If your redirect URIs must use `localhost`, set `auth.redirectHost` to `localhost` in the profile.

Users then need only the endpoint URL and the client id:

```bash
abp-mcp-bridge profiles add staging --url https://staging-api.example.com/mcp --auth browser --client-id MyApp_Mcp \
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
(RFC 8707) when signing in. Without it, profiles set `auth.authority` and `auth.scope`, and `doctor` says so.

### The read-only preset needs `/mcp/_discover`

Production profiles default to the `read-only` preset, which shows only tools backed by GET endpoints. The bridge
reads each tool's HTTP method from `/mcp/_discover`, which `MapAbpMcp` serves. If `_discover` is unavailable to the
user, the preset hides every tool rather than guess.

## How the bridge decides to retry

A request is sent again only when the app refused it before running anything:

- **401**: the token expired or was revoked. The bridge renews it (refresh token, then the profile's credentials) and
  sends the request once more.
- **404 with a session id**: the app ended the MCP session (idle timeout, restart). The bridge opens a new session and
  sends the request once more.

Anything else after a `tools/call` was sent (a dropped connection, a broken response stream, a timeout, a 5xx) may
mean the call ran, so the bridge returns `BRIDGE_CALL_OUTCOME_UNKNOWN` and never re-sends it. `tools/list` and
`initialize` change nothing, so they are retried once on a fresh session after any transport failure.

## Releasing the bridge

The bridge is versioned separately from the NuGet package.

1. Update `bridge/package.json`'s `version` and add a `## [X.Y.Z]` section to `bridge/CHANGELOG.md`.
2. Merge, then push a tag `bridge-vX.Y.Z`. (The NuGet release workflow takes `v*` tags, so the two never collide.)
3. `.github/workflows/release-bridge.yml` tests the package, publishes `@tekthar/abp-mcp-bridge` to npm with
   provenance, builds the Claude Desktop extension, and creates a GitHub Release with the `.mcpb` attached and the
   changelog section as notes.

npm publishing uses trusted publishing (OIDC, no long-lived token): on npmjs.com, add a trusted publisher for the
package with repository `tekthar/abp-mcp` and workflow `release-bridge.yml`. npm only allows that once the package
exists, so the very first release needs an `NPM_TOKEN` repository secret (an automation token); remove it afterwards.

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

The `Bridge` workflow runs all of it on every pull request that touches `bridge/`, `src/` or `samples/`.
