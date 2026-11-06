using System.Security.Claims;
using System.Text.Json.Nodes;
using AbpMcp.Authorization;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task UndefinedPermission_RefusesTheTool_InsteadOfThrowing()
    {
        // #84: ASP.NET Core throws "No policy found" for an undefined name, which failed tools/list for everyone.
        var authorizer = CreateAuthorizer();
        var descriptor = Describe(typeof(MisconfiguredService), nameof(MisconfiguredService.Typo));

        var result = await authorizer.AuthorizeAsync(descriptor, Context(User(perms: new[] { "Perm.Undefined" })));

        result.Succeeded.Should().BeFalse("an undefined permission must never grant access");
        result.FailureReason.Should().Be("Missing permission: Perm.Undefined (it is not defined on this server).");
    }

    [Fact]
    public async Task UndefinedPolicy_OnlyInAuthorizeData_IsRefusedToo()
    {
        // An add-in can attach [Authorize] data without listing its policy as a permission.
        var authorizer = CreateAuthorizer();
        var descriptor = Describe(typeof(GuardedService), nameof(GuardedService.Both)) with
        {
            RequiredPermissions = Array.Empty<string>(),
            AuthorizeData = new IAuthorizeData[] { new AuthorizeAttribute("Perm.Undefined") },
        };

        var result = await authorizer.AuthorizeAsync(descriptor, Context(User()));

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("Perm.Undefined");
    }

    [Fact]
    public async Task UndefinedPermission_IsLoggedOncePerTool()
    {
        var logger = new RecordingLogger();
        var authorizer = CreateAuthorizer(new UndefinedPermissionLog(logger));
        var descriptor = Describe(typeof(MisconfiguredService), nameof(MisconfiguredService.Typo));

        for (var i = 0; i < 3; i++)
        {
            await authorizer.AuthorizeAsync(descriptor, Context(User()));
        }

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Typo").And.Contain("Perm.Undefined");
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

    private static AbpMcpToolAuthorizer CreateAuthorizer(UndefinedPermissionLog? undefinedPermissions = null)
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
            provider.GetRequiredService<IAuthorizationPolicyProvider>(),
            undefinedPermissions ?? new UndefinedPermissionLog(NullLogger<UndefinedPermissionLog>.Instance));
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

    private sealed class MisconfiguredService
    {
        [Authorize("Perm.Undefined")]
        public void Typo() { }
    }

    private sealed class RecordingLogger : ILogger<UndefinedPermissionLog>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
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
