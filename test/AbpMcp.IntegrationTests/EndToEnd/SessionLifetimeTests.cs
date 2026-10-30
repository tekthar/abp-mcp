using System.Runtime.CompilerServices;
using AbpMcp.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Sessions the SDK has ended must not be kept alive by abp-mcp's list_changed session registry,
/// or every connect/disconnect grows the server's memory for the life of the process.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class SessionLifetimeTests : IAsyncLifetime
{
    private const int SessionCount = 20;

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

            // End the session the way a client closing it does (streamable HTTP: DELETE with the session id).
            using var end = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
            end.Headers.Add("Mcp-Session-Id", client.SessionId);
            (await http.SendAsync(end)).EnsureSuccessStatusCode();
        }

        var alive = SessionCount;
        for (var attempt = 0; attempt < 20 && alive > 0; attempt++)
        {
            alive = CollectAndCount(registry);
            await Task.Delay(50);
        }

        alive.Should().Be(0, "ended sessions must not be kept alive by the registry");
    }

    /// <summary>
    /// Collect, then count, in a frame of its own: a Debug JIT keeps temporaries alive for the whole method,
    /// so a session snapshot taken in the caller would itself root the sessions across the collection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CollectAndCount(McpSessionRegistry registry)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return registry.Sessions.Count;
    }
}
