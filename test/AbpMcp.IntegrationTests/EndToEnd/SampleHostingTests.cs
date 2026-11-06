using AbpMcp.Sample.Library;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Authorization.Permissions;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// The sample runs on the Autofac container, as a standard ABP host does. Without it ABP's OnRegistered hooks never
/// ran: the sample's permission definition provider was not collected (Member_Delete required an "undefined"
/// permission) and application services got none of ABP's interceptors.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class SampleHostingTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task PermissionDefinitionProvider_IsCollectedByAbp()
    {
        var definitions = _host.Services.GetRequiredService<IPermissionDefinitionManager>();

        (await definitions.GetOrNullAsync(LibraryPermissions.MembersDelete)).Should().NotBeNull();
    }

    [Fact]
    public void ApplicationServices_AreInterceptedByAbp()
    {
        using var scope = _host.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IMemberAppService>();

        service.GetType().Should().NotBe(typeof(MemberAppService), "Autofac wraps application services in ABP's interceptor proxy");
    }
}
