using AbpMcp.Attributes;
using AbpMcp.Metadata;

namespace AbpMcp.Listing;

/// <summary>
/// Decides whether a tool is listed in <c>tools/list</c> or on demand, and why. The one place that applies the
/// precedence documented on <see cref="ToolListingOptions"/>; the registry stores the result on each descriptor
/// (<see cref="ToolDescriptor.Listing"/>), and <c>tools/list</c>, the gateway and the diagnostics endpoints read it there.
/// </summary>
internal interface IToolListingResolver
{
    /// <summary>
    /// The effective listing of <paramref name="descriptor"/>, whose <see cref="ToolDescriptor.Listing"/> holds the
    /// declared value (from <c>[McpTool(Listing = ...)]</c> or an add-in), with the host's patterns applied.
    /// </summary>
    ToolListingDecision Resolve(ToolDescriptor descriptor);
}

/// <summary>The listing <see cref="IToolListingResolver"/> chose for a tool, and the rule that chose it.</summary>
/// <param name="Listing">Listed in <c>tools/list</c>, or on demand.</param>
/// <param name="Reason">Which rule decided, as <c>/mcp/_explain</c> reports it.</param>
internal readonly record struct ToolListingDecision(McpToolListing Listing, string Reason);
