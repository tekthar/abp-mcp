namespace AbpMcp.Gateway;

/// <summary>
/// The names of the gateway tools <c>tools/list</c> advertises whenever at least one tool is on demand
/// (<see cref="Attributes.McpToolListing.OnDemand"/>). They are a wire contract and stay byte-stable. The names are
/// reserved: a real tool with one of them fails startup with <see cref="AbpMcpConfigurationException"/>.
/// </summary>
/// <remarks>
/// The gateway tools are never on demand, are not prefixed with <see cref="AbpMcpOptions.ToolNamePrefix"/>, need no
/// permission of their own (each finds, describes and calls only what the caller may use), and honour the kill
/// switch (<see cref="AbpMcpOptions.DisabledTools"/> and the <c>AbpMcp.DisabledTools</c> setting) like any tool.
/// </remarks>
public static class AbpMcpGatewayToolNames
{
    /// <summary><c>mcp_search_tools(query, limit?)</c>: ranked matches across every tool the caller may use.</summary>
    public const string Search = "mcp_search_tools";

    /// <summary><c>mcp_describe_tools(names)</c>: the description and input and output schemas of the named tools.</summary>
    public const string Describe = "mcp_describe_tools";

    /// <summary><c>mcp_call_tool(name, arguments)</c>: calls one tool, returning its own result.</summary>
    public const string Call = "mcp_call_tool";

    /// <summary><c>mcp_call_tools(calls, mode)</c>: calls up to 20 tools in one request.</summary>
    public const string CallMany = "mcp_call_tools";

    /// <summary>Every gateway tool name, in the order <c>tools/list</c> advertises them.</summary>
    public static IReadOnlyList<string> All { get; } = [Search, Describe, Call, CallMany];

    /// <summary>True when <paramref name="name"/> is one of the gateway tool names (ordinal comparison).</summary>
    public static bool IsGatewayTool(string? name) =>
        name is not null && All.Contains(name, StringComparer.Ordinal);
}
