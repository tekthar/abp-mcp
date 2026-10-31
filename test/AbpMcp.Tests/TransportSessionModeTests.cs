using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace AbpMcp.Tests;

/// <summary>
/// AddAbpMcp pins the MCP transport to stateful sessions (SDK 2.x defaults to stateless, which cannot
/// deliver tools/list_changed), and a host's own transport configuration still wins.
/// </summary>
public class TransportSessionModeTests
{
    [Fact]
    public void AddAbpMcp_PinsStatefulSessions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAbpMcp();

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateful);
    }

    [Fact]
    public void HostConfiguration_OverridesThePinnedMode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAbpMcp();
        services.Configure<HttpServerTransportOptions>(o => o.SessionMode = HttpServerSessionMode.Stateless);

        Resolve(services).SessionMode.Should().Be(HttpServerSessionMode.Stateless);
    }

    private static HttpServerTransportOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
}
