using AbpMcp.Registration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AbpMcp.Notifications;

/// <summary>
/// Default <see cref="IAbpMcpToolListChangedNotifier"/>: rebuilds the registry, then broadcasts
/// <c>notifications/tools/list_changed</c> to every tracked session, pruning any that fail to receive.
/// </summary>
internal sealed class AbpMcpToolListChangedNotifier : IAbpMcpToolListChangedNotifier
{
    private readonly IDynamicMcpToolRegistry _registry;
    private readonly McpSessionRegistry _sessions;
    private readonly ILogger<AbpMcpToolListChangedNotifier> _logger;

    public AbpMcpToolListChangedNotifier(
        IDynamicMcpToolRegistry registry,
        McpSessionRegistry sessions,
        ILogger<AbpMcpToolListChangedNotifier> logger)
    {
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task NotifyToolListChangedAsync(CancellationToken cancellationToken = default)
    {
        // Rebuild first so a client that re-reads tools/list immediately sees the new set.
        _registry.Refresh();

        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
        {
            return;
        }

        var notified = 0;
        foreach (var server in sessions)
        {
            try
            {
                await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken)
                    .ConfigureAwait(false);
                notified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort fan-out: a send fails when the session is gone — drop it and keep going so
                // one dead session can't abort the broadcast. Caller cancellation (OCE) is NOT swallowed.
                _sessions.Remove(server);
                _logger.LogDebug(ex, "abp-mcp dropped a dead MCP session while broadcasting tools/list_changed.");
            }
        }

        _logger.LogDebug("abp-mcp broadcast tools/list_changed to {Count} session(s).", notified);
    }
}
