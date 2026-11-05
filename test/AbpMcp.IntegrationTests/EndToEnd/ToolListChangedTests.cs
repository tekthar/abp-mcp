using System.Runtime.CompilerServices;
using AbpMcp.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// notifications/tools/list_changed must reach a connected agent. Server-initiated notifications need a
/// stateful session, so this pins the transport mode: MCP SDK 2.x defaults to stateless, under which
/// <see cref="IAbpMcpToolListChangedNotifier"/> would silently stop reaching connected agents.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class ToolListChangedTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>
    /// The server can push a notification only over the client's standalone GET stream, which the client opens
    /// on its own after connecting. A notification sent before that stream is open is lost, so the test waits
    /// for it. Without the wait the test raced the client: it passed where the stream opens fast and timed out
    /// on slower machines. The delayed case reproduces that race deterministically.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)] // a slow machine: the client opens its notification stream a second late
    public async Task Notifier_DeliversToolListChanged_ToAConnectedClient(int streamDelayMs)
    {
        var stream = new NotificationStreamProbe(TimeSpan.FromMilliseconds(streamDelayMs));
        await using var client = await _host.ConnectAsync(stream);
        client.SessionId.Should().NotBeNullOrEmpty("the MCP transport must run in stateful mode");

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                received.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await client.ListToolsAsync();
        await stream.Opened.WaitAsync(TimeSpan.FromSeconds(10));
        await _host.Services.GetRequiredService<IAbpMcpToolListChangedNotifier>().NotifyToolListChangedAsync();

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Regression for #83: the session registry held sessions weakly, through the SDK's per-request server
    /// wrapper, so a garbage collection between the agent's last request and the broadcast dropped a live
    /// session and the notification never left the server. Under a full test run that GC happened often enough
    /// to fail the test above at random; this forces it.
    /// </summary>
    [Fact]
    public async Task Notifier_ReachesALiveSession_AfterAGarbageCollection()
    {
        var stream = new NotificationStreamProbe(TimeSpan.Zero);
        await using var client = await _host.ConnectAsync(stream);

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                received.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await client.ListToolsAsync();
        await stream.Opened.WaitAsync(TimeSpan.FromSeconds(10));
        CollectGarbage();
        await _host.Services.GetRequiredService<IAbpMcpToolListChangedNotifier>().NotifyToolListChangedAsync();

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A collection in a frame of its own, so no temporary of the caller's survives it by accident.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Signals once the server has accepted the client's standalone GET stream, the channel server-initiated
    /// notifications travel on, optionally delaying the client's GET request first.
    /// </summary>
    private sealed class NotificationStreamProbe(TimeSpan delay) : DelegatingHandler
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _opened.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            await Task.Delay(delay, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _opened.TrySetResult();
            }

            return response;
        }
    }
}
