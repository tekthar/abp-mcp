# Letting Claude sign users in: MCP OAuth for ABP + OpenIddict

Claude Code, Claude Desktop and claude.ai custom connectors can run the browser sign-in themselves, without a pasted
token and without [the bridge](bridge.md), if your host follows the
[MCP authorization spec](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization). This guide sets
that up for an ABP solution with an API host (where `/mcp` runs) and a separate OpenIddict AuthServer.

About ten pieces are involved, on two hosts. Each one fails in a way that is hard to trace back (`invalid_target`, a 403
straight after sign-in, a token that unlocks the whole REST API), so every step below says what goes wrong without it.
The end of the guide has a [checklist](#9-checking-a-deployment) to verify a deployment.

**Direct sign-in or the bridge?** Direct sign-in needs nothing on the user's machine, but it needs the steps below,
and claude.ai connectors must reach your host from the internet. [The bridge](bridge.md) works with the OpenIddict
clients you already have, reaches intranet and VPN-only hosts, and can limit tools per environment. The two work side by
side.

The examples use these names. Replace them with yours:

| Placeholder | Meaning |
|---|---|
| `https://api.example.com/mcp` | The MCP endpoint on the API host: the **resource** (RFC 8707) |
| `https://auth.example.com/` | The AuthServer's **issuer**, exactly as its discovery document prints it |
| `MyApp` | The REST API's existing scope and audience |
| `MyAppMcp` | A new scope and audience used only for MCP tokens |
| `MyApp_Claude` | The new OpenIddict client that Claude signs in with |

```text
Claude ──POST /mcp (no token)──────────► API host ──401 WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/mcp"
Claude ──GET  /.well-known/oauth-protected-resource/mcp ► { resource, authorization_servers: [issuer], scopes_supported }
Claude ──GET  <issuer>/.well-known/… (AS metadata) ─► AuthServer
Claude ──/connect/authorize?…&resource=https://api.example.com/mcp&scope=… (PKCE) ─► AuthServer: sign-in, consent
Claude ──/connect/token ─► access token  aud=MyAppMcp  scope="… MyAppMcp"
Claude ──POST /mcp  Authorization: Bearer … ─► API host: MCP scheme → JwtBearer → policy (scope MyAppMcp) → tools
```

## 1. Protected-resource metadata and the challenge (API host)

`ModelContextProtocol.AspNetCore` (the SDK abp-mcp builds on, so it is already referenced) has an authentication scheme
for this. `AddMcp` registers the scheme `McpAuthenticationDefaults.AuthenticationScheme` (`"McpAuth"`), which:

- forwards authentication to the `Bearer` scheme (`ForwardAuthenticate` defaults to `"Bearer"`, the name ABP's
  `AddAbpJwtBearer` uses), so it validates nothing itself;
- serves your metadata at `/.well-known/oauth-protected-resource/mcp` (RFC 9728);
- challenges with `WWW-Authenticate: Bearer resource_metadata="https://api.example.com/.well-known/oauth-protected-resource/mcp"`.

In the API host module's `ConfigureServices`, next to the JwtBearer setup the ABP template already has:

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

var configuration = context.Services.GetConfiguration();
var issuer = configuration["AuthServer:Authority"]!;   // "https://auth.example.com/": the issuer, trailing slash included
var mcpResource = "https://api.example.com/mcp";        // the public URL of /mcp

context.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddAbpJwtBearer(options =>
    {
        options.Authority = issuer;
        options.Audience = "MyApp";
        // Step 2 adds ValidAudiences and OnTokenValidated here.
    })
    .AddMcp(options =>
    {
        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = mcpResource,
            AuthorizationServers = { issuer },
            ScopesSupported = { "openid", "profile", "roles", "offline_access", "MyAppMcp" },
            ResourceName = "MyApp",
        };
    });
```

Three things to get right:

- **The policy on `/mcp` must name only the MCP scheme** (step 3 does this). If it names no scheme, the challenge
  comes from JwtBearer and is a bare `WWW-Authenticate: Bearer` with no `resource_metadata`, so Claude has nothing to
  discover. If it names both schemes, the response carries two `WWW-Authenticate` headers.
- **`authorization_servers` must equal the AuthServer's issuer exactly**, trailing slash included. Clients compare it
  with the `issuer` in the AuthServer's metadata, and RFC 8414 requires an exact match. Copy the value from
  `https://auth.example.com/.well-known/openid-configuration`.
- **`resource` must be the URL clients connect to**, scheme, host, port and path. It is also the value you register
  with OpenIddict in step 4.

`BearerMethodsSupported` already contains `"header"` by default. Do not add it again, or the document lists it twice.

This guide assumes the API host validates tokens with JwtBearer, as ABP's tiered and separate-AuthServer templates do.
A host that validates with OpenIddict's validation handler instead (typically one host that is also the AuthServer)
must set `options.ForwardAuthenticate` to that scheme (`OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`)
and add the audience with `AddAudiences("MyAppMcp")`; step 2's `OnTokenValidated` hook then needs an OpenIddict
validation event handler instead. That variant is not covered here.

**Behind a reverse proxy or load balancer that terminates TLS**, the handler builds the `resource_metadata` URL from
the incoming request, so it comes out as `http://…` or with an internal host name. Either enable forwarded headers
(`app.UseForwardedHeaders()` with `ForwardedHeaders.XForwardedProto | XForwardedHost`, configured for your proxy), or
set the URL outright:

```csharp
options.ResourceMetadataUri = new Uri("https://api.example.com/.well-known/oauth-protected-resource/mcp");
```

**The bridge is strict about these values.** When it discovers the sign-in from your host, [the bridge](bridge.md)
refuses any discovered OAuth URL that is not `https`, checks that the metadata's `resource` matches the MCP URL it was
given, and checks that the AuthServer metadata's `issuer` matches `authorization_servers[0]` (it tolerates a trailing
slash difference; other clients may not). A host that publishes inconsistent values fails there first.

## 2. Keep MCP tokens and REST tokens apart

Without this, an access token Claude obtained can call your whole REST API, and the token your Angular or Blazor app
holds can drive `/mcp`. Give MCP tokens their own audience, accept it, and confine it to `/mcp`:

1. A dedicated scope, `MyAppMcp`, whose resource is `MyAppMcp` (step 4 creates it). ABP's `AuthorizeController` sets
   the token's `aud` from the resources of the granted scopes, so an MCP token has `aud: "MyAppMcp"`.
2. The API host accepts that audience, and rejects a token whose **only** audience is `MyAppMcp` anywhere outside
   `/mcp`.
3. The `/mcp` policy requires the `MyAppMcp` scope (step 3), so a REST token gets 403 there.

Extend the `AddAbpJwtBearer` call from step 1:

```csharp
.AddAbpJwtBearer(options =>
{
    options.Authority = issuer;
    options.Audience = "MyApp";
    options.TokenValidationParameters.ValidAudiences = new[] { "MyApp", McpTokenRules.Audience };

    var previous = options.Events.OnTokenValidated;
    options.Events.OnTokenValidated = async ctx =>
    {
        await previous(ctx);
        McpTokenRules.RejectOutsideMcp(ctx);
    };
})
```

```csharp
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

public static class McpTokenRules
{
    public const string Audience = "MyAppMcp";
    public const string Scope = "MyAppMcp";
    public const string PolicyName = "MyAppMcp";
    public const string McpPath = "/mcp";   // AbpMcpOptions.Path

    private static readonly string[] ScopeClaimTypes =
    {
        "scope",
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    };

    /// <summary>A token issued only for MCP may not call anything outside /mcp.</summary>
    public static void RejectOutsideMcp(TokenValidatedContext ctx)
    {
        if (ctx.Result is not null)   // an earlier handler already decided
        {
            return;
        }

        var audiences = ctx.SecurityToken is JsonWebToken jwt
            ? jwt.Audiences.ToList()
            : ctx.Principal!.FindAll("aud").Select(c => c.Value).ToList();

        var mcpOnly = audiences.Count > 0 && audiences.All(a => a == Audience);
        if (mcpOnly && !ctx.HttpContext.Request.Path.StartsWithSegments(McpPath))
        {
            ctx.Fail("This token was issued for the MCP endpoint only.");
        }
    }

    /// <summary>True when the principal was granted the scope, under any of the usual claim types.</summary>
    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Claims
            .Where(c => ScopeClaimTypes.Contains(c.Type))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
```

`StartsWithSegments("/mcp")` covers `/mcp`, `/mcp/_discover` and `/mcp/_explain`, but not `/mcpfoo`. The scope check
reads `scope`, `scp` and the Microsoft scope claim type because, depending on the token handler and
`MapInboundClaims`, the same claim arrives under different names, sometimes as one space-separated value.

A token that also carries `MyApp` passes the audience rule. That is why the client in step 5 must **not** be granted
the REST scope.

## 3. Put `/mcp` and the diagnostics behind one policy

Define the policy, then hand its name to abp-mcp:

```csharp
// using Microsoft.AspNetCore.Authorization;
Configure<AuthorizationOptions>(options =>
{
    options.AddPolicy(McpTokenRules.PolicyName, policy => policy
        .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)  // only the MCP scheme
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => McpTokenRules.HasScope(ctx.User, McpTokenRules.Scope)));
});

Configure<AbpMcpOptions>(options =>
{
    options.AuthorizationPolicy = McpTokenRules.PolicyName;   // /mcp, /mcp/_discover and /mcp/_explain
});
```

`AbpMcpOptions.AuthorizationPolicy` applies the policy to `/mcp`, `/mcp/_discover` and `/mcp/_explain` together. Leave
`AllowAnonymous` off. The diagnostics endpoints list your whole tool surface, so they belong behind the same policy as
`/mcp`: a REST token should get 403 on `/mcp/_discover` too.

**On a version without `AuthorizationPolicy`**, put `MapAbpMcp` in a route group that carries the policy. Group and
endpoint policies are combined, and the combined policy authenticates only with the MCP scheme:

```csharp
app.UseConfiguredEndpoints(endpoints =>
{
    endpoints.MapGroup("").RequireAuthorization(McpTokenRules.PolicyName).MapAbpMcp();
});
```

## 4. OpenIddict on the AuthServer

Claude sends the MCP URL as the `resource` parameter (RFC 8707) on the authorization and token requests. OpenIddict
rejects a resource it does not know with `invalid_target`, so register it in the AuthServer module:

```csharp
public override void PreConfigureServices(ServiceConfigurationContext context)
{
    PreConfigure<OpenIddictServerBuilder>(builder =>
    {
        builder.RegisterResources("https://api.example.com/mcp");

        // Either let every client name a registered resource...
        // builder.IgnoreResourcePermissions();
        // ...or, as in step 5, grant the client an "rsrc:" permission for it.
    });
}
```

Create the scope in your `OpenIddictDataSeedContributor`, next to the `MyApp` scope:

```csharp
if (await _scopeManager.FindByNameAsync("MyAppMcp") == null)
{
    await _scopeManager.CreateAsync(new OpenIddictScopeDescriptor
    {
        Name = "MyAppMcp",
        DisplayName = "MyApp for AI agents",
        Resources = { "MyAppMcp" },   // becomes the token's aud
    });
}
```

The `resource` parameter is validated, but it does not set the audience: ABP's `AuthorizeController` sets `aud` from
the granted scopes' resources. So the token's audience is `MyAppMcp` from this scope, not the URL. That is what step 2
expects.

## 5. The client Claude signs in with

One public client serves Claude Code, Claude Desktop and claude.ai. It uses the authorization code flow with PKCE and
refresh tokens, and gets the MCP scope but **not** the REST scope:

```csharp
if (await _applicationManager.FindByClientIdAsync("MyApp_Claude") == null)
{
    await _applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
    {
        ClientId = "MyApp_Claude",
        ClientType = OpenIddictConstants.ClientTypes.Public,
        ConsentType = OpenIddictConstants.ConsentTypes.Explicit,   // see below
        DisplayName = "Claude",
        RedirectUris =
        {
            // Claude Code: pin the port with --callback-port; OpenIddict matches ports exactly.
            new Uri("http://localhost:33418/callback"),
            new Uri("http://127.0.0.1:33418/callback"),
            // claude.ai and Claude Desktop custom connectors.
            new Uri("https://claude.ai/api/mcp/auth_callback"),
        },
        Permissions =
        {
            OpenIddictConstants.Permissions.Endpoints.Authorization,
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,   // also what allows offline_access
            OpenIddictConstants.Permissions.ResponseTypes.Code,
            OpenIddictConstants.Permissions.Scopes.Profile,
            OpenIddictConstants.Permissions.Scopes.Roles,
            OpenIddictConstants.Permissions.Prefixes.Scope + "MyAppMcp",
            // Not Prefixes.Scope + "MyApp": that would let this token call the REST API.
            "rsrc:https://api.example.com/mcp",   // unless the AuthServer calls IgnoreResourcePermissions()
        },
        Requirements =
        {
            OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,   // "ft:pkce"
        },
    });
}
```

**Explicit consent is recommended.** The client is public and one of its redirect URIs belongs to a third party
(claude.ai). With implicit consent, anyone can craft an authorize link for this client that completes without a prompt
for a user who is already signed in to your AuthServer. With explicit consent, the user sees which application asks for
access and approves it. Claude Code always sends `prompt=consent` and adds `offline_access`, so with explicit consent
the consent page appears on every interactive sign-in. Refresh tokens keep that rare.

### Connecting each client

**Claude Code.** Pass the client id and the port you registered:

```bash
claude mcp add --transport http --client-id MyApp_Claude --callback-port 33418 my-app https://api.example.com/mcp
```

Then run `/mcp` in Claude Code and choose the server to sign in. Register more than one port (and pass a different
`--callback-port`) if users may run several sign-ins at once.

**claude.ai and Claude Desktop.** Add a custom connector with the URL `https://api.example.com/mcp`, and enter
`MyApp_Claude` as the OAuth client id in the connector's advanced settings (OpenIddict does not offer dynamic client
registration out of the box, so Claude cannot register a client of its own). Hosted connectors connect from Anthropic's
servers, so the API host and the AuthServer must be reachable from the internet over `https`. For an intranet host, use
[the bridge](bridge.md).

## 6. Origins

abp-mcp refuses a request whose `Origin` header is not in `AbpMcpOptions.AllowedOrigins`, as the MCP transport spec
requires (see [Browser-based clients](../README.md#browser-based-clients)). If hosted connectors send
`Origin: https://claude.ai`, allow it:

```csharp
Configure<AbpMcpOptions>(options => options.AllowedOrigins.Add("https://claude.ai"));
```

The origin check runs after authorization, so a refused origin shows up only after a successful sign-in: a 403 with
`Origin not allowed. Add it to AbpMcpOptions.AllowedOrigins.` in the body. That is easy to mistake for the scope check
from step 3, so look at the response body.

## 7. More than one instance

By default abp-mcp keeps MCP sessions in memory, so behind a load balancer a request can reach an instance that never
saw the session and get 404. Use session affinity, or switch to the stateless transport (which gives up
`tools/list_changed` notifications). See
[Running more than one instance](../README.md#running-more-than-one-instance).

## 8. Caveat: a deleted user's MCP token keeps working until it expires

ABP's `UseDynamicClaims` middleware refreshes the signed-in principal from the current user data on every request, so
the REST API treats a deleted user as anonymous straight away, even while their access token is still valid.

`/mcp` does not get that. When an endpoint's policy names an authentication scheme (as step 3 must, for the
challenge), ASP.NET Core's authorization middleware authenticates again with that scheme and **replaces** the principal
that `UseDynamicClaims` produced with the one built from the token. So on `/mcp`, `/mcp/_discover` and `/mcp/_explain`,
a deleted user's access token keeps working until it expires. By the same mechanism, other claim changes that
`UseDynamicClaims` would pick up (roles, for instance) reach `/mcp` only with the next token.

Refresh tokens are not affected: the AuthServer refuses to renew a token for a deleted user. To narrow the window, give
`MyApp_Claude` short-lived access tokens; Claude renews them with the refresh token. With OpenIddict 5 or later, a
per-client lifetime can go in the descriptor:

```csharp
Settings =
{
    [OpenIddictConstants.Settings.TokenLifetimes.AccessToken] =
        TimeSpan.FromMinutes(10).ToString("c", CultureInfo.InvariantCulture),
},
```

## 9. Checking a deployment

Run these against the deployed hosts. Each item names what is wrong when it fails.

```bash
API=https://api.example.com
MCP=$API/mcp
AUTH=https://auth.example.com
ACCEPT='Accept: application/json, text/event-stream'
JSON='Content-Type: application/json'
LIST='{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

**1. An unauthenticated `POST /mcp` gets exactly one challenge, with `resource_metadata`, over https.**

```bash
curl -si -X POST "$MCP" -H "$JSON" -H "$ACCEPT" -d "$LIST" | grep -i '^www-authenticate'
# WWW-Authenticate: Bearer resource_metadata="https://api.example.com/.well-known/oauth-protected-resource/mcp"
```

No `resource_metadata`: the `/mcp` policy names no scheme or the wrong one (step 3). Two lines: it names both schemes.
An `http://` URL or an internal host name: the reverse-proxy note in step 1.

**2. The metadata is consistent with the MCP URL and the AuthServer.**

```bash
RM=$(curl -s "$API/.well-known/oauth-protected-resource/mcp")
echo "$RM" | jq
[ "$(echo "$RM" | jq -r .resource)" = "$MCP" ] && echo "resource ok" || echo "resource MISMATCH"

ISSUER=$(curl -s "$AUTH/.well-known/openid-configuration" | jq -r .issuer)
[ "$(echo "$RM" | jq -r '.authorization_servers[0]')" = "$ISSUER" ] && echo "issuer ok" || echo "issuer MISMATCH: $ISSUER"
echo "$RM" | jq -e '.scopes_supported | index("MyAppMcp")' >/dev/null && echo "scope ok" || echo "scope missing"
```

**3. An MCP token has the right `aud` and `scope`.** Get one by hand, with the same client and PKCE (any free port
you registered):

```bash
VERIFIER=$(openssl rand -base64 48 | tr -d '=+/\n' | cut -c1-64)
CHALLENGE=$(printf %s "$VERIFIER" | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '=')
REDIRECT=http://127.0.0.1:33418/callback
echo "$AUTH/connect/authorize?client_id=MyApp_Claude&response_type=code&redirect_uri=$REDIRECT&scope=openid%20profile%20roles%20offline_access%20MyAppMcp&code_challenge=$CHALLENGE&code_challenge_method=S256&resource=$MCP"
# Open it, sign in, approve. Nothing listens on the port, so copy the code= value from the address bar.
CODE=...
MCP_TOKEN=$(curl -s "$AUTH/connect/token" -d grant_type=authorization_code -d client_id=MyApp_Claude \
  -d "code=$CODE" -d "redirect_uri=$REDIRECT" -d "code_verifier=$VERIFIER" --data-urlencode "resource=$MCP" \
  | jq -r .access_token)

jwt_payload() { local p; p=$(cut -d. -f2 <<<"$1" | tr '_-' '/+'); while (( ${#p} % 4 )); do p+='='; done; base64 -d <<<"$p"; }
jwt_payload "$MCP_TOKEN" | jq '{aud, scope}'
# { "aud": "MyAppMcp", "scope": "openid profile roles offline_access MyAppMcp" }   (scope may be an array)
```

`invalid_target`: the resource is not registered (step 4). `aud` includes `MyApp`: the client was granted the REST
scope (step 5). `aud` missing: the `MyAppMcp` scope has no resource (step 4).

**4. The MCP token is useless on the REST API.**

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $MCP_TOKEN" "$API/api/account/my-profile"   # 401
curl -s -H "Authorization: Bearer $MCP_TOKEN" "$API/api/abp/application-configuration" | jq .currentUser.isAuthenticated   # false
```

A 200 or `true`: the `OnTokenValidated` rule is missing or did not run (step 2).

**5. A REST token is refused on `/mcp` and on the diagnostics.** Use a token your SPA or Swagger UI got for `MyApp`:

```bash
curl -s -o /dev/null -w '/mcp %{http_code}\n' -X POST "$MCP" -H "Authorization: Bearer $REST_TOKEN" -H "$JSON" -H "$ACCEPT" -d "$LIST"
curl -s -o /dev/null -w '/mcp/_discover %{http_code}\n' -H "Authorization: Bearer $REST_TOKEN" "$API/mcp/_discover"
# /mcp 403
# /mcp/_discover 403
```

A 200 on `/mcp`: the policy does not require the scope. A 200 on `_discover` only: the policy is not applied to the
diagnostics (step 3).

**6. The MCP token works.**

```bash
curl -s -X POST "$MCP" -H "Authorization: Bearer $MCP_TOKEN" -H "$JSON" -H "$ACCEPT" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}' -i \
  | grep -i -E '^HTTP|^mcp-session-id'
curl -s -H "Authorization: Bearer $MCP_TOKEN" "$API/mcp/_discover" | jq '.tools | length'
```

In the stateless transport (step 7), `tools/list` must also work on its own, with no `initialize` first:

```bash
curl -s -X POST "$MCP" -H "Authorization: Bearer $MCP_TOKEN" -H "$JSON" -H "$ACCEPT" -d "$LIST" | head -c 300
```

**7. A browser-origin request from claude.ai is accepted** (only if you added it in step 6):

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H 'Origin: https://claude.ai' -H "Authorization: Bearer $MCP_TOKEN" "$API/mcp/_discover"   # 200, not 403
```

The origin check runs after authorization, so test it with a valid token: without one, every request gets 401 first.

Finally, connect Claude Code as in step 5 and check that `/mcp` shows the server as connected with your tools.
