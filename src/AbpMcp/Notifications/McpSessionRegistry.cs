using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;

namespace AbpMcp.Notifications;

// Note: the SDK session type is the McpServer class (RequestContext.Server), not an interface.

/// <summary>
/// Tracks the live MCP server sessions so server-initiated notifications (e.g.
/// <c>notifications/tools/list_changed</c>) can be broadcast to every connected agent.
/// </summary>
/// <remarks>
/// The low-level handler path does not expose a session list, so the abp-mcp handler wiring
/// records each session's <see cref="McpServer"/> the first time it handles a request for it.
/// Sessions are held weakly: once the SDK ends a session (client DELETE, idle timeout) and drops it,
/// it is collected and disappears from <see cref="Sessions"/> with no disconnect hook needed. Holding
/// them strongly would keep every session ever opened alive for the life of the process. A session
/// that is ended but not yet collected fails its send and is removed by the notifier. Server-initiated
/// notifications require a non-stateless transport; with the stateless transport each request is its
/// own session and there is nothing to notify later.
/// </remarks>
internal sealed class McpSessionRegistry
{
    private static readonly object Tracked = new();

    private readonly ConditionalWeakTable<McpServer, object> _sessions = new();

    /// <summary>Record a session. Idempotent.</summary>
    public void Track(McpServer server) => _sessions.TryAdd(server, Tracked);

    /// <summary>Forget a session (e.g. after a failed send).</summary>
    public void Remove(McpServer server) => _sessions.Remove(server);

    /// <summary>A snapshot of the sessions that are still alive.</summary>
    public IReadOnlyCollection<McpServer> Sessions => _sessions.Select(entry => entry.Key).ToArray();
}
