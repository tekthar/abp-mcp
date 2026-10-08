using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace AbpMcp.Notifications;

// Note: the SDK session type is the McpServer class (RequestContext.Server), not an interface.

/// <summary>
/// Tracks the live MCP server sessions so server-initiated notifications (e.g.
/// <c>notifications/tools/list_changed</c>) can be broadcast to every connected agent.
/// </summary>
/// <remarks>
/// The low-level handler path does not expose a session list, so the abp-mcp handler wiring
/// records each session's <see cref="McpServer"/> the first time it handles a request for it. Dead
/// sessions are pruned lazily — a send that throws removes the session — which avoids needing a
/// disconnect hook. Server-initiated notifications require a non-stateless transport; with the
/// stateless transport each request is its own session and there is nothing to notify later.
/// </remarks>
internal sealed class McpSessionRegistry
{
    private readonly ConcurrentDictionary<McpServer, byte> _sessions = new();

    /// <summary>Record a session. Idempotent.</summary>
    public void Track(McpServer server) => _sessions.TryAdd(server, 0);

    /// <summary>Forget a session (e.g. after a failed send).</summary>
    public void Remove(McpServer server) => _sessions.TryRemove(server, out _);

    /// <summary>A snapshot of the currently tracked sessions.</summary>
    public IReadOnlyCollection<McpServer> Sessions => _sessions.Keys.ToArray();
}
