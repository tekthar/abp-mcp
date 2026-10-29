using System.Security.Claims;
using System.Text.Json.Nodes;
using AbpMcp.Authorization;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.Tests;

/// <summary>
/// Regression: tool authorization used to keep only <c>[Authorize(Policy)]</c> names, with a method
/// policy <em>replacing</em> the class policy, and silently dropped <c>Roles</c>, schemes and
/// policy-less (authenticated-only) <c>[Authorize]</c>. Because the dispatcher invokes the concrete
/// service by reflection, no MVC filter or ABP interceptor backfilled those, so tools/list and
/// tools/call enforced less than the parallel HTTP endpoint. These tests run the real ASP.NET Core
/// authorization service against descriptors built by the real harvester.
/// </summary>
public class ToolAuthorizationTests
{
    [Fact]
    public void ResolveAuthorization_UnionsClassAndMethodPolicies()
    {
        var (permissions, data) = Resolve(typeof(GuardedService), nameof(GuardedService.Both));

        permissions.Should().BeEquivalentTo("Perm.Method", "Perm.Class");
        data.Should().HaveCount(2);
    }

    [Fact]
    public void ResolveAuthorization_KeepsRolesAndPolicyLessAuthorize()
    {
        var (adminPermissions, adminData) = Resolve(typeof(RoleService), nameof(RoleService.AdminOnly));
        adminPermissions.Should().BeEmpty();
        adminData.Should().ContainSingle().Which.Roles.Should().Be("admin");

        var (_, authenticatedData) = Resolve(typeof(RoleService), nameof(RoleService.AuthenticatedOnly));
        authenticatedData.Should().ContainSingle();
    }

    [Fact]
    public void ResolveAuthorization_MethodAllowAnonymousClearsRequirements()
    {
        var (permissions, data) = Resolve(typeof(GuardedService), nameof(GuardedService.Anonymous));

        permissions.Should().BeEmpty();
        data.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassPolicyIsEnforcedEvenWhenMethodDeclaresItsOwn()
    {
        var authorizer = CreateAuthorizer();
        var descriptor = Describe(typeof(GuardedService), nameof(GuardedService.Both));

        var methodOnly = await authorizer.AuthorizeAsync(descriptor, Context(User(perms: new[] { "Perm.Method" })));
        methodOnly.Succeeded.Should().BeFalse();
        methodOnly.FailureReason.Should().Be("Missing permission: Perm.Class");

        var both = await authorizer.AuthorizeAsync(descriptor, Context(User(perms: new[] { "Perm.Method", "Perm.Class" })));
        both.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RolesAreEnforced()
    {
        var authorizer = CreateAuthorizer();
        var descriptor = Describe(typeof(RoleService), nameof(RoleService.AdminOnly));

        var withoutRole = await authorizer.AuthorizeAsync(descriptor, Context(User()));
        withoutRole.Succeeded.Should().BeFalse();
        withoutRole.FailureReason.Should().Contain("admin");

        (await authorizer.AuthorizeAsync(descriptor, Context(User(roles: new[] { "admin" })))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task PolicyLessAuthorizeRequiresAnAuthenticatedCaller()
    {
        var authorizer = CreateAuthorizer();
        var descriptor = Describe(typeof(RoleService), nameof(RoleService.AuthenticatedOnly));

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        (await authorizer.AuthorizeAsync(descriptor, Context(anonymous))).Succeeded.Should().BeFalse();
        (await authorizer.AuthorizeAsync(descriptor, Context(User()))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MissingCallerIdentityFailsClosedOnlyForToolsWithRequirements()
    {
        var authorizer = CreateAuthorizer();

        (await authorizer.AuthorizeAsync(Describe(typeof(RoleService), nameof(RoleService.AuthenticatedOnly)), null))
            .Succeeded.Should().BeFalse();
        (await authorizer.AuthorizeAsync(Describe(typeof(RoleService), nameof(RoleService.Open)), null))
            .Succeeded.Should().BeTrue();
    }

    private static (IReadOnlyList<string> Permissions, IReadOnlyList<IAuthorizeData> Data) Resolve(Type type, string methodName) =>
        ToolDescriptorBuilder.ResolveAuthorization(type, type.GetMethod(methodName)!);

    private static ToolDescriptor Describe(Type type, string methodName)
    {
        var (permissions, data) = Resolve(type, methodName);
        return new ToolDescriptor
        {
            Name = methodName,
            Description = methodName,
            ServiceType = type,
            Method = type.GetMethod(methodName),
            InputSchema = new JsonObject(),
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = permissions,
            AuthorizeData = data,
        };
    }

    private static AbpMcpToolAuthorizer CreateAuthorizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy("Perm.Class", p => p.RequireClaim("perm", "Perm.Class"));
            options.AddPolicy("Perm.Method", p => p.RequireClaim("perm", "Perm.Method"));
        });
        var provider = services.BuildServiceProvider();

        return new AbpMcpToolAuthorizer(
            provider.GetRequiredService<IAuthorizationService>(),
            provider.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private static HttpContext Context(ClaimsPrincipal user) => new DefaultHttpContext { User = user };

    private static ClaimsPrincipal User(string[]? perms = null, string[]? roles = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange((perms ?? Array.Empty<string>()).Select(p => new Claim("perm", p)));
        claims.AddRange((roles ?? Array.Empty<string>()).Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    [Authorize("Perm.Class")]
    private sealed class GuardedService
    {
        [Authorize("Perm.Method")]
        public void Both() { }

        [AllowAnonymous]
        public void Anonymous() { }
    }

    private sealed class RoleService
    {
        [Authorize(Roles = "admin")]
        public void AdminOnly() { }

        [Authorize]
        public void AuthenticatedOnly() { }

        public void Open() { }
    }
}
