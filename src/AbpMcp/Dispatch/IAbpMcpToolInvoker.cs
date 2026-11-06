using System.Text.Json;

namespace AbpMcp.Dispatch;

/// <summary>
/// Calls a registered tool by name: the kill switch, the name lookup, then <see cref="IAbpMcpDispatcher"/> (permission
/// check, validation, feature check, unit of work, audit, error mapping). A direct <c>tools/call</c>,
/// <c>mcp_call_tool</c> and each call of <c>mcp_call_tools</c> all go through it, so they behave identically.
/// </summary>
internal interface IAbpMcpToolInvoker
{
    /// <summary>Invoke <paramref name="toolName"/> with <paramref name="arguments"/> (a JSON object).</summary>
    /// <returns>The outcome; a coded error (never an exception) for a disabled, unknown, refused or failing tool.</returns>
    Task<AbpMcpToolCallOutcome> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);
}
