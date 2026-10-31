using AbpMcp.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// notifications/tools/list_changed must reach a connected agent. Server-initiated notifications need a
/// stateful session, so this pins the transport mode: MCP SDK 2.x defaults to stateless, under which the
/// DisabledTools kill switch and <see cref="IAbpMcpToolListChangedNotifier"/> would silently stop working.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class ToolListChangedTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Notifier_DeliversToolListChanged_ToAConnectedClient()
    {
        await using var client = await _host.ConnectAsync();
        client.SessionId.Should().NotBeNullOrEmpty("the MCP transport must run in stateful mode");

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                received.TrySetResult();
                return ValueTask.CompletedTask;
            });

        // A request registers the session with abp-mcp's list_changed session registry.
        await client.ListToolsAsync();
        await _host.Services.GetRequiredService<IAbpMcpToolListChangedNotifier>().NotifyToolListChangedAsync();

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
