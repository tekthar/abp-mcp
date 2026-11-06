using System.Reflection;
using AbpMcp.Attributes;
using AbpMcp.Metadata;
using Microsoft.Extensions.Options;

namespace AbpMcp.Listing;

/// <summary>
/// Default <see cref="IToolListingResolver"/>: a <see cref="ToolListingOptions.Listed"/> pin, then an
/// <see cref="ToolListingOptions.OnDemand"/> pattern, then the declared listing, then the default (listed).
/// </summary>
internal sealed class ToolListingResolver : IToolListingResolver
{
    private readonly ToolListingOptions _options;

    public ToolListingResolver(IOptions<AbpMcpOptions> options) => _options = options.Value.ToolListing;

    public ToolListingDecision Resolve(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var pin = ToolNameGlob.FirstMatch(_options.ListedPatterns, descriptor.Name);
        if (pin is not null)
        {
            return new(McpToolListing.Listed,
                $"Listed: AbpMcpOptions.ToolListing.Listed(\"{pin}\") pins it; a pin wins over OnDemand patterns and [McpTool(Listing = ...)].");
        }

        var onDemand = ToolNameGlob.FirstMatch(_options.OnDemandPatterns, descriptor.Name);
        if (onDemand is not null)
        {
            return new(McpToolListing.OnDemand,
                $"On demand: AbpMcpOptions.ToolListing.OnDemand(\"{onDemand}\") matches; the host's patterns win over [McpTool(Listing = ...)].");
        }

        if (descriptor.Listing == McpToolListing.OnDemand)
        {
            return new(McpToolListing.OnDemand, DescribeDeclaredOnDemand(descriptor));
        }

        return new(McpToolListing.Listed,
            "Listed: the default. No AbpMcpOptions.ToolListing pattern matches, and [McpTool] does not set Listing = McpToolListing.OnDemand.");
    }

    /// <summary>The wire name of a listing, as <c>/mcp/_discover</c> and <c>mcp_search_tools</c> report it.</summary>
    public static string WireName(McpToolListing listing) =>
        listing == McpToolListing.OnDemand ? "on_demand" : "listed";

    /// <summary>
    /// Where a declared on-demand listing came from: the <c>[McpTool]</c> that exposes the method (the method's, else
    /// the class's, as the reader picks it), or, when that attribute does not say OnDemand, the add-in that set it.
    /// </summary>
    private static string DescribeDeclaredOnDemand(ToolDescriptor descriptor)
    {
        var onMethod = descriptor.Method?.GetCustomAttribute<McpToolAttribute>(inherit: true);
        var onClass = descriptor.ServiceType?.GetCustomAttribute<McpToolAttribute>(inherit: true);
        var effective = onMethod ?? onClass;

        if (effective?.Listing == McpToolListing.OnDemand)
        {
            var where = onMethod is not null
                ? $"method {descriptor.ServiceType?.Name}.{descriptor.Method!.Name}"
                : $"class {descriptor.ServiceType!.Name}";
            return $"On demand: [McpTool(Listing = McpToolListing.OnDemand)] on {where}.";
        }

        return "On demand: set on the tool's descriptor (ToolDescriptor.Listing), by the add-in that contributed or enriched it.";
    }
}
