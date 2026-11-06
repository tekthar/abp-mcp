using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// <see cref="AbpMcpOptions.AuthorizationPolicy"/> guards <c>/mcp</c> and both diagnostics endpoints with one
/// policy (#96). Before it, <c>MapAbpMcp().RequireAuthorization("McpOnly")</c> covered only <c>/mcp</c>, and any
/// token the API accepted could still read the full tool catalogue from <c>_discover</c>.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class AuthorizationPolicyTests
{
    private const string Policy = "McpOnly";
    private const string ScopeHeader = "X-Test-Scope";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""";

    public static TheoryData<string> Endpoints => new() { "/mcp", "/mcp/_discover", "/mcp/_explain" };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TokenLackingThePolicy_IsForbidden_OnEveryEndpoint(string path)
    {
        await using var host = await StartAsync(o => o.AuthorizationPolicy = Policy);

        (await SendAsync(host, path, scope: "admin-spa")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TokenSatisfyingThePolicy_IsAllowed_OnEveryEndpoint(string path)
    {
        await using var host = await StartAsync(o => o.AuthorizationPolicy = Policy);

        (await SendAsync(host, path, scope: "mcp")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Unauthenticated_IsChallenged_OnEveryEndpoint(string path)
    {
        await using var host = await StartAsync(o => o.AuthorizationPolicy = Policy);

        (await SendAsync(host, path, scope: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithoutAPolicy_AnyAuthenticatedCallerPasses(string path)
    {
        // Unchanged default: the host's default policy, which any authenticated caller satisfies.
        await using var host = await StartAsync(_ => { });

        (await SendAsync(host, path, scope: "admin-spa")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AllowAnonymous_IgnoresThePolicy(string path)
    {
        await using var host = await StartAsync(o =>
        {
            o.AllowAnonymous = true;
            o.AuthorizationPolicy = Policy;
        });

        (await SendAsync(host, path, scope: null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<SampleHost> StartAsync(Action<AbpMcpOptions> configure) =>
        SampleHost.StartAsync(
            o =>
            {
                o.AllowAnonymous = false; // the sample allows anonymous calls; these tests need real auth
                configure(o);
            },
            builder =>
            {
                builder.Services
                    .AddAuthentication(ScopeHeaderAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ScopeHeaderAuthenticationHandler>(
                        ScopeHeaderAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization(o =>
                    o.AddPolicy(Policy, p => p.RequireAuthenticatedUser().RequireClaim("scope", "mcp")));
            });

    private static async Task<HttpResponseMessage> SendAsync(SampleHost host, string path, string? scope)
    {
        using var http = host.Server.CreateClient();
        using var request = path == "/mcp"
            ? new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") }
            : new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (scope is not null)
        {
            request.Headers.Add(ScopeHeader, scope);
        }

        return await http.SendAsync(request);
    }

    /// <summary>Stands in for a bearer token: authenticates any request carrying a scope header, with that scope as a claim.</summary>
    private sealed class ScopeHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestScope";

        public ScopeHeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // The SDK binds a stateful session to the caller's user id ('sub'), and refuses users without one.
            var identity = new ClaimsIdentity(
                [new Claim("sub", "agent-" + scope), new Claim(ClaimTypes.Name, "agent"), new Claim("scope", scope.ToString())],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
