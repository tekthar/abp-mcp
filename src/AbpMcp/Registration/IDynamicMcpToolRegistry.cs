using AbpMcp.Metadata;

namespace AbpMcp.Registration;

/// <summary>
/// Owns the process of reading descriptors from <see cref="IApiDefinitionReader"/>
/// and registering them as runtime MCP tools with the Microsoft SDK.
/// </summary>
public interface IDynamicMcpToolRegistry
{
    /// <summary>Called once at application startup. Idempotent.</summary>
    void Initialize();

    /// <summary>All currently registered tools. Useful for introspection and tests.</summary>
    IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>
    /// Look up a tool by its MCP-facing name. Used by the dispatcher when an
    /// incoming <c>tools/call</c> arrives and we need to resolve the descriptor.
    /// </summary>
    /// <returns><c>true</c> if a tool with that name is known to the registry.</returns>
    bool TryGetByName(string toolName, out ToolDescriptor? descriptor);
}
