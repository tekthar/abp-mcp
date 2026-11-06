using System.Reflection;
using AbpMcp.Listing;
using AbpMcp.Metadata;

namespace AbpMcp.Diagnostics;

/// <summary>
/// The listing half of <c>/mcp/_explain</c>: for each exposed method, the tool it became, whether that tool is listed or
/// on demand, and the rule that decided it, read from the registry where <see cref="IToolListingResolver"/> stored it.
/// </summary>
internal static class ToolListingExplainer
{
    /// <summary>The registry's method-backed tools, keyed by the method they invoke.</summary>
    public static Dictionary<MethodInfo, ToolDescriptor> IndexByMethod(IEnumerable<ToolDescriptor> tools)
    {
        var index = new Dictionary<MethodInfo, ToolDescriptor>();
        foreach (var tool in tools)
        {
            if (tool.Method is not null)
            {
                index.TryAdd(tool.Method, tool);
            }
        }

        return index;
    }

    /// <summary>One <c>decisions</c> row; an included method also gets <c>tool</c>, <c>listing</c> and <c>listing_reason</c>.</summary>
    public static object DecisionRow(
        Type serviceType,
        MethodInfo method,
        bool included,
        string reason,
        IReadOnlyDictionary<MethodInfo, ToolDescriptor> toolsByMethod)
    {
        if (!included)
        {
            return new { service = serviceType.FullName, method = method.Name, included, reason };
        }

        if (!toolsByMethod.TryGetValue(method, out var tool))
        {
            return new
            {
                service = serviceType.FullName,
                method = method.Name,
                included,
                reason,
                tool = (string?)null,
                listing = (string?)null,
                listing_reason = "Not in the current tool set: an add-in removed it (AbpMcpToolBuildContext.RemoveTool).",
            };
        }

        return new
        {
            service = serviceType.FullName,
            method = method.Name,
            included,
            reason,
            tool = tool.Name,
            listing = ToolListingResolver.WireName(tool.Listing),
            listing_reason = tool.ListingReason,
        };
    }

    /// <summary>The tools no service method backs (contributed by add-ins), with their listing and its reason.</summary>
    public static List<object> DynamicToolRows(IEnumerable<ToolDescriptor> tools) =>
        tools
            .Where(tool => tool.Method is null)
            .Select(tool => (object)new
            {
                tool = tool.Name,
                listing = ToolListingResolver.WireName(tool.Listing),
                listing_reason = tool.ListingReason,
            })
            .ToList();
}
