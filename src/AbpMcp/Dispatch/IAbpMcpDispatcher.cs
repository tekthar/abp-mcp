using System.Text.Json;
using AbpMcp.Metadata;

namespace AbpMcp.Dispatch;

/// <summary>
/// Executes an MCP tool call by resolving the underlying ABP service from DI,
/// enforcing permissions, invoking the method, and mapping the result (or exception)
/// back to the MCP protocol shape.
/// </summary>
public interface IAbpMcpDispatcher
{
    /// <summary>
    /// Invoke the tool described by <paramref name="descriptor"/> with the arguments in <paramref name="arguments"/>.
    /// </summary>
    /// <param name="descriptor">The tool being invoked. Built once at startup, reused forever.</param>
    /// <param name="arguments">Parsed JSON object sent by the agent in the MCP <c>tools/call</c> request.</param>
    /// <param name="cancellationToken">Cancellation signal, wired to the agent's transport.</param>
    /// <returns>The result to place in the MCP tool response's <c>content</c> block.</returns>
    Task<JsonElement> InvokeAsync(
        ToolDescriptor descriptor,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
