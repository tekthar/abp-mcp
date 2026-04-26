using System.Reflection;
using AbpMcp.Attributes;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Metadata;

/// <summary>
/// Builds a single <see cref="ToolDescriptor"/> from an ABP action description and its resolved <see cref="MethodInfo"/>.
/// </summary>
public interface IToolDescriptorBuilder
{
    /// <summary>Build the descriptor for one exposed method.</summary>
    ToolDescriptor Build(
        Type serviceType,
        MethodInfo method,
        ActionApiDescriptionModel action,
        McpToolAttribute expose,
        AbpMcpOptions options);
}
