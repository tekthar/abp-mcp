namespace AbpMcp.Metadata;

/// <summary>
/// Reads ABP's application API description model and produces the subset of
/// services that are eligible for MCP exposure (opted-in with <c>[McpTool]</c>).
/// </summary>
public interface IApiDefinitionReader
{
    /// <summary>
    /// Scans the host application for MCP-exposed application service methods.
    /// </summary>
    /// <returns>Descriptors for every exposed method. May be empty.</returns>
    IReadOnlyList<ToolDescriptor> Read();
}
