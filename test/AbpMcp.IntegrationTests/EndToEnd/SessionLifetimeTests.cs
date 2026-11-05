using AbpMcp.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// abp-mcp's list_changed session registry holds a session exactly while its notification stream is open:
/// sessions the SDK has ended must not stay in memory, and live ones must not drop out.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class SessionLifetimeTests : IAsyncLifetime
{
    private const int SessionCount = 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task EndedSessions_AreReleased()
    {
        var registry = _host.Services.GetRequiredService<McpSessionRegistry>();
        using var http = _host.Server.CreateClient();

        for (var i = 0; i < SessionCount; i++)
        {
            await using var client = await _host.ConnectAsync();
            await client.ListToolsAsync();
            await WaitForAsync(() => registry.Sessions.Count == 1, "a connected session is tracked while its stream is open");

            // End the session the way a client closing it does (streamable HTTP: DELETE with the session id).
            using var end = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
            end.Headers.Add("Mcp-Session-Id", client.SessionId);
            (await http.SendAsync(end)).EnsureSuccessStatusCode();

            await WaitForAsync(() => registry.Sessions.Count == 0, "an ended session must not be kept by the registry");
        }
    }

    [Fact]
    public async Task DisconnectedClients_AreReleased()
    {
        var registry = _host.Services.GetRequiredService<McpSessionRegistry>();

        for (var i = 0; i < SessionCount; i++)
        {
            await using (var client = await _host.ConnectAsync())
            {
                await client.ListToolsAsync();
                await WaitForAsync(() => registry.Sessions.Count == 1, "a connected session is tracked while its stream is open");
            }

            await WaitForAsync(() => registry.Sessions.Count == 0, "a disconnected client's session must not be kept by the registry");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue(because);
    }
}
