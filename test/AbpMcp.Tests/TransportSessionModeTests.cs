using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using NSubstitute;
using Volo.Abp.Modularity;

namespace AbpMcp.Tests;

/// <summary>
/// AddAbpMcp pins the MCP transport to stateful sessions (SDK 2.x defaults to stateless, which cannot
/// deliver tools/list_changed) unless <see cref="AbpMcpOptions.Stateless"/> asks otherwise (#100), and a
/// host's own transport configuration still wins over both.
/// </summary>
public class TransportSessionModeTests
{
    [Fact]
    public void AddAbpMcp_PinsStatefulSessions()
    {
        var services = NewServices();
        services.AddAbpMcp();

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateful);
    }

    [Fact]
    public void HostConfiguration_OverridesThePinnedMode()
    {
        var services = NewServices();
        services.AddAbpMcp();
        services.Configure<HttpServerTransportOptions>(o => o.SessionMode = HttpServerSessionMode.Stateless);

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateless);
    }

    [Fact]
    public void StatelessOption_SelectsStatelessSessions()
    {
        var services = NewServices();
        services.AddAbpMcp(o => o.Stateless = true);

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateless);
    }

    [Fact]
    public void StatelessOption_SetAfterAddAbpMcp_IsHonored()
    {
        // An ABP host module configures AbpMcpOptions in its own ConfigureServices, after AbpMcpModule ran AddAbpMcp.
        var services = NewServices();
        services.AddAbpMcp();
        services.Configure<AbpMcpOptions>(o => o.Stateless = true);

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateless);
    }

    [Fact]
    public void HostConfiguration_OverridesTheStatelessOption()
    {
        var services = NewServices();
        services.AddAbpMcp(o => o.Stateless = true);
        services.Configure<HttpServerTransportOptions>(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.StatefulForInitializeClients);
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Resolving AbpMcpOptions runs module-graph auto-discovery, which an ABP host always provides.
        var modules = Substitute.For<IModuleContainer>();
        modules.Modules.Returns(Array.Empty<IAbpModuleDescriptor>());
        services.AddSingleton(modules);
        return services;
    }

    private static HttpServerTransportOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
}
