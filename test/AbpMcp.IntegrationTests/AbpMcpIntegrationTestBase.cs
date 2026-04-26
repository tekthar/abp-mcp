using System.Security.Claims;
using AbpMcp.IntegrationTests.Books;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;

namespace AbpMcp.IntegrationTests;

/// <summary>
/// Base class for integration tests. Boots the ABP module hierarchy via
/// <see cref="AbpIntegratedTest{TStartupModule}"/>, exposes the root service
/// provider, and provides a helper to invoke tools as a specific user against
/// a per-test request scope. Each test gets its own DI scope, its own
/// <see cref="BookDbContext"/> instance, and its own request context.
/// </summary>
public abstract class AbpMcpIntegrationTestBase : AbpIntegratedTest<AbpMcpTestModule>
{
    protected AbpMcpIntegrationTestBase()
    {
    }

    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        // Be explicit: this test base assumes Autofac (the AbpAutofacModule depend-on alone
        // does not opt the app in to using Autofac as the container).
        options.UseAutofac();
    }

    /// <summary>
    /// Run a delegate inside a per-test DI scope, with the dispatcher pre-wired to a
    /// <see cref="ClaimsPrincipal"/> that the dispatcher's permission gate will see.
    /// </summary>
    protected async Task<T> RunAsAuthenticatedAsync<T>(
        Func<IServiceProvider, Task<T>> body,
        ClaimsPrincipal? user = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var scope = ServiceProvider.CreateScope();
        var accessor = (TestHttpContextAccessor)scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        accessor.PushAuthenticatedContext(scope.ServiceProvider, user ?? DefaultUser());

        return await body(scope.ServiceProvider).ConfigureAwait(false);
    }

    /// <summary>
    /// Default authenticated principal used by tests that do not care about a specific identity.
    /// Uses ABP claim type names so any future ABP-aware code path resolves the identity correctly.
    /// Real-user permission scenarios construct their own <see cref="ClaimsPrincipal"/>.
    /// </summary>
    protected static ClaimsPrincipal DefaultUser()
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        identity.AddClaim(new Claim(Volo.Abp.Security.Claims.AbpClaimTypes.UserId, Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim(Volo.Abp.Security.Claims.AbpClaimTypes.UserName, "integration-test-user"));
        return new ClaimsPrincipal(identity);
    }
}
