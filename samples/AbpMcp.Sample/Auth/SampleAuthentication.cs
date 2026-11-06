using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using Volo.Abp.Security.Claims;

namespace AbpMcp.Sample.Auth;

/// <summary>
/// The authenticated mode of the sample, set up as <c>docs/oauth.md</c> steps 1 and 3 describe: JwtBearer
/// validates tokens from <see cref="SampleAuthOptions.Issuer"/>, the MCP SDK's scheme serves the RFC 9728
/// metadata and the challenge, and one policy (naming only the MCP scheme and requiring the scope) covers
/// <c>/mcp</c>, <c>/mcp/_discover</c> and <c>/mcp/_explain</c> through <see cref="AbpMcpOptions.AuthorizationPolicy"/>.
/// </summary>
public static class SampleAuthentication
{
    /// <summary>The policy on the MCP endpoints.</summary>
    public const string PolicyName = "LibraryMcp";

    private static readonly string[] ScopeClaimTypes =
    [
        "scope",
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    ];

    /// <summary>Register authentication and the MCP policy for <paramref name="auth"/>.</summary>
    public static void Configure(IServiceCollection services, SampleAuthOptions auth)
    {
        var issuer = auth.Issuer!;
        var resource = auth.Resource
            ?? throw new InvalidOperationException($"{SampleAuthOptions.Section}:Resource must be set with {SampleAuthOptions.Section}:Issuer.");

        // Tokens carry JWT claim names ("sub", not the mapped NameIdentifier); ABP reads the user id from "sub".
        AbpClaimTypes.UserId = "sub";
        AbpClaimTypes.UserName = "preferred_username";
        AbpClaimTypes.Email = "email";
        AbpClaimTypes.Role = "role";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddAbpJwtBearer(options =>
            {
                options.Authority = issuer;
                options.Audience = auth.Audience;
                options.MapInboundClaims = false;
                // Plain http only for a loopback issuer, as the bridge itself allows.
                options.RequireHttpsMetadata = !IsLoopback(issuer);
            })
            .AddMcp(options =>
            {
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = resource,
                    AuthorizationServers = { issuer },
                    ScopesSupported = { auth.Scope, "offline_access" },
                    ResourceName = "Library sample",
                };
            });

        services.Configure<AuthorizationOptions>(options =>
        {
            options.AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasScope(ctx.User, auth.Scope)));
        });
    }

    /// <summary>True when the principal was granted <paramref name="scope"/>, under any of the usual claim types.</summary>
    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Claims
            .Where(c => ScopeClaimTypes.Contains(c.Type))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
