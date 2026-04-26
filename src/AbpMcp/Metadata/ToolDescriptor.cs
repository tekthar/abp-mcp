using System.Reflection;
using System.Text.Json.Nodes;

namespace AbpMcp.Metadata;

/// <summary>
/// An MCP tool description resolved from an ABP application service method.
/// Consumed by the dispatcher to route tool calls and advertise the tool to clients.
/// </summary>
/// <remarks>
/// Tool descriptors are immutable and built once at startup. All reflection happens here
/// so the dispatcher hot path can use <see cref="MethodInfo"/> directly without scanning.
/// </remarks>
public sealed record ToolDescriptor
{
    /// <summary>MCP tool name, after prefix and naming convention have been applied.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description advertised to the agent. Pulled from <c>[McpTool(Description=...)]</c> or a mechanical fallback.</summary>
    public required string Description { get; init; }

    /// <summary>The application service type that implements this tool. Resolved from DI at invocation time.</summary>
    public required Type ServiceType { get; init; }

    /// <summary>The specific method on the service to invoke.</summary>
    public required MethodInfo Method { get; init; }

    /// <summary>JSON Schema for the tool's input object.</summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>Ordered names of the parameters the method expects. Used to map JSON input into positional args.</summary>
    public required IReadOnlyList<string> ParameterNames { get; init; }

    /// <summary>Permission names required to call this tool. Empty means no permission required beyond authentication.</summary>
    public required IReadOnlyList<string> RequiredPermissions { get; init; }
}
