namespace AbpMcp.Notifications;

/// <summary>
/// The hook to call when the MCP tool set has changed at runtime (for example, a plugin's tools
/// appearing or disappearing). It rebuilds the tool registry and sends MCP's
/// <c>notifications/tools/list_changed</c> to connected agents so they re-read <c>tools/list</c>
/// without reconnecting.
/// </summary>
/// <remarks>
/// Resolve it from DI and call <see cref="NotifyToolListChangedAsync"/> after mutating whatever your
/// add-in or plugin uses to decide the tool set. The default implementation refreshes
/// <see cref="Registration.IDynamicMcpToolRegistry"/> and broadcasts to every tracked session;
/// delivery requires a non-stateless HTTP transport (the default).
/// </remarks>
public interface IAbpMcpToolListChangedNotifier
{
    /// <summary>Refresh the tool registry and notify connected clients that the tool list changed.</summary>
    Task NotifyToolListChangedAsync(CancellationToken cancellationToken = default);
}
