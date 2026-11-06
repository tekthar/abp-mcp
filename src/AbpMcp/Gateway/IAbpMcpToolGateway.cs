using System.Text.Json;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;

namespace AbpMcp.Gateway;

/// <summary>
/// The gateway tools (<see cref="AbpMcpGatewayToolNames"/>) that let an agent reach on-demand tools: search, describe,
/// and call one or several through <see cref="IAbpMcpToolInvoker"/>, the path a direct <c>tools/call</c> takes. On only
/// while at least one registered tool is on demand.
/// </summary>
/// <remarks>
/// Search and describe filter through <c>IAbpMcpToolVisibility</c>, the gate <c>tools/list</c> uses, so the gateway
/// never reveals a tool the caller could not see in <c>tools/list</c> were it listed. The gateway runs no code of its
/// own on the agent's behalf: no expression or script evaluation (design rule 5); code mode belongs in a client such
/// as the bridge.
/// </remarks>
internal interface IAbpMcpToolGateway
{
    /// <summary>The gateway tools to advertise in <c>tools/list</c>: all four while the gateway is on, else none.</summary>
    IReadOnlyList<ToolDescriptor> GetListedTools();

    /// <summary>True when <paramref name="toolName"/> is a gateway tool and the gateway is on.</summary>
    bool Handles(string toolName);

    /// <summary>Run the gateway tool <paramref name="toolName"/>; <see cref="Handles"/> must be true for it.</summary>
    Task<AbpMcpToolCallOutcome> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);
}
