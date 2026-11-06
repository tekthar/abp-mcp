namespace AbpMcp.Attributes;

/// <summary>
/// How an exposed tool is advertised to MCP clients. Set it with <c>[McpTool(Listing = ...)]</c>; a host can
/// override it without touching the service through <c>AbpMcpOptions.ToolListing</c>, which wins over the attribute.
/// </summary>
/// <remarks>
/// Every tool in <c>tools/list</c> costs the agent context (name, description, input and output schema) before
/// the first message. <see cref="OnDemand"/> keeps a rarely used tool out of that list: the agent finds it with the
/// <c>mcp_search_tools</c> gateway tool, reads its schema with <c>mcp_describe_tools</c> and calls it with
/// <c>mcp_call_tool</c> or <c>mcp_call_tools</c>. Permissions, the kill switch, validation, unit of work and
/// auditing apply to an on-demand tool exactly as to a listed one.
/// </remarks>
public enum McpToolListing
{
    /// <summary>Advertised in <c>tools/list</c>. The default.</summary>
    Listed = 0,

    /// <summary>
    /// Not advertised in <c>tools/list</c>. Found through <c>mcp_search_tools</c> and called through
    /// <c>mcp_call_tool</c> / <c>mcp_call_tools</c> (or by name with <c>tools/call</c>).
    /// </summary>
    OnDemand = 1,
}
