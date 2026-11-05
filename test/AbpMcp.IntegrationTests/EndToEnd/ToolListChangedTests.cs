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

        // A request registers the session with abp-mcp's list_changed session registry.
        await client.ListToolsAsync();
        await stream.Opened.WaitAsync(TimeSpan.FromSeconds(10));
        await _host.Services.GetRequiredService<IAbpMcpToolListChangedNotifier>().NotifyToolListChangedAsync();

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
