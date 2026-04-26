namespace AbpMcp.Attributes;

/// <summary>
/// Marks an ABP application service type or method as exposed through the MCP endpoint.
/// Exposure is opt-in by default. A service or method without this attribute is not
/// visible to MCP clients.
/// </summary>
/// <remarks>
/// This attribute mirrors the role of <c>[RemoteService(IsEnabled = ...)]</c> in ABP's
/// conventional controllers world: a single, declarative knob developers reach for first.
///
/// Placement semantics:
/// <list type="bullet">
///   <item>On a class implementing <c>IApplicationService</c>: exposes the whole service.
///     Methods can still be hidden individually with <see cref="McpIgnoreAttribute"/>.</item>
///   <item>On a method: exposes only that method. The containing service is implicitly included.</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class McpToolAttribute : Attribute
{
    /// <summary>Optional override for the MCP tool name. Bypasses the configured <c>ToolNameNormalizer</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Optional override for the tool description. When null, the default mechanical description is used.</summary>
    public string? Description { get; init; }
}
