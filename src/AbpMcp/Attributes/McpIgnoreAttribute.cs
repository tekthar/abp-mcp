namespace AbpMcp.Attributes;

/// <summary>
/// Suppresses MCP exposure of an otherwise-exposed service type or method.
/// Useful when a class is decorated with <see cref="McpToolAttribute"/> at the type level
/// but one of its methods should not be callable by an agent.
/// </summary>
/// <remarks>
/// This attribute mirrors <c>[RemoteService(IsEnabled = false)]</c> in ABP's conventional
/// controllers: an explicit, declarative override that always wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class McpIgnoreAttribute : Attribute
{
}
