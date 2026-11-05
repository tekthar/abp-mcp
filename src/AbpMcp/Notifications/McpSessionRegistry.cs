using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace AbpMcp.Notifications;

/// <summary>
/// Tracks the MCP sessions that can currently receive server-initiated notifications (e.g.
/// <c>notifications/tools/list_changed</c>), so they can be broadcast to every connected agent.
/// </summary>
/// <remarks>
/// <para>
/// Over streamable HTTP a server can push to a client only through the client's standalone GET stream; with no
/// stream open the SDK drops the notification. So the registry tracks exactly those streams:
/// <see cref="McpNotificationStreamTracker"/> adds a session when the SDK accepts its GET stream and removes it
/// when that request ends, however it ends (client disconnect, <c>DELETE</c>, idle timeout, shutdown).
/// </para>
/// <para>
/// Each entry is the SDK's session object, held strongly but never longer than the request that keeps the
/// session open. Earlier versions held the per-request server wrapper weakly, which let a garbage collection
/// drop a live session (#83), and before that held it strongly forever (#57).
/// </para>
/// </remarks>
internal sealed class McpSessionRegistry
{
    private readonly ConcurrentDictionary<object, McpServer> _streams = new();

    /// <summary>
    /// Record an open notification stream for <paramref name="server"/>. Dispose the returned handle when the
    /// stream closes.
    /// </summary>
    public IDisposable Add(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var key = new object();
        _streams[key] = server;
        return new Registration(_streams, key);
    }

    /// <summary>
    /// A snapshot of the sessions with an open notification stream, each listed once even if a client
    /// resumed its stream alongside the original.
    /// </summary>
    public IReadOnlyCollection<McpServer> Sessions =>
        _streams.Values.Distinct(ReferenceEqualityComparer.Instance).Cast<McpServer>().ToArray();

    private sealed class Registration(ConcurrentDictionary<object, McpServer> streams, object key) : IDisposable
    {
        public void Dispose() => streams.TryRemove(key, out _);
    }
}
