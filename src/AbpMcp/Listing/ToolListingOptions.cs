using AbpMcp.Attributes;

namespace AbpMcp.Listing;

/// <summary>
/// Host-side overrides of how tools are advertised (<see cref="AbpMcpOptions.ToolListing"/>): name patterns that keep
/// tools out of <c>tools/list</c> (<see cref="OnDemand"/>) or pin them in it (<see cref="Listed"/>), without touching
/// the services.
/// </summary>
/// <remarks>
/// Precedence, highest first:
/// <list type="number">
///   <item>a <see cref="Listed"/> pattern (a pin) matches: the tool is listed;</item>
///   <item>an <see cref="OnDemand"/> pattern matches: the tool is on demand;</item>
///   <item><c>[McpTool(Listing = ...)]</c> (on the method, else on the class), or the
///     <see cref="Metadata.ToolDescriptor.Listing"/> an add-in set for its own tool;</item>
///   <item>the default, <see cref="McpToolListing.Listed"/>.</item>
/// </list>
/// Patterns match the whole tool name, after <see cref="AbpMcpOptions.ToolNamePrefix"/> and any
/// <c>[McpTool(Name = ...)]</c> override, case-sensitively. <c>*</c> matches any run of characters (including none)
/// and <c>?</c> exactly one; every other character matches itself. The listing is decided when the tool set is built
/// (at startup and on <see cref="Registration.IDynamicMcpToolRegistry.Refresh"/>), so a change made later applies
/// after the next refresh. The gateway tools themselves are never on demand. Works in any
/// <c>Configure&lt;AbpMcpOptions&gt;</c>, including one in <c>PreConfigureServices</c>.
/// </remarks>
/// <example>
/// <code>
/// Configure&lt;AbpMcpOptions&gt;(o =>
/// {
///     o.ToolListing.OnDemand("Jira*", "*_Delete");   // keep these out of tools/list
///     o.ToolListing.Listed("Jira_GetIssue");         // but keep this one listed: pins win
/// });
/// </code>
/// </example>
public sealed class ToolListingOptions
{
    private readonly List<string> _onDemand = new();
    private readonly List<string> _listed = new();

    /// <summary>The patterns registered with <see cref="OnDemand"/>, in registration order.</summary>
    public IReadOnlyList<string> OnDemandPatterns => _onDemand;

    /// <summary>The patterns registered with <see cref="Listed"/>, in registration order.</summary>
    public IReadOnlyList<string> ListedPatterns => _listed;

    /// <summary>
    /// Make every tool whose name matches one of <paramref name="patterns"/> on demand: left out of <c>tools/list</c>,
    /// found through <c>mcp_search_tools</c>, called through <c>mcp_call_tool</c>. Wins over
    /// <c>[McpTool(Listing = Listed)]</c>; loses to a <see cref="Listed"/> pin.
    /// </summary>
    /// <param name="patterns">Tool names or globs (<c>*</c>, <c>?</c>), matched case-sensitively.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="ArgumentException">A pattern is null, empty or white space.</exception>
    public ToolListingOptions OnDemand(params string[] patterns)
    {
        Add(_onDemand, patterns);
        return this;
    }

    /// <summary>
    /// Pin every tool whose name matches one of <paramref name="patterns"/> in <c>tools/list</c>. A pin wins over every
    /// <see cref="OnDemand"/> pattern and over <c>[McpTool(Listing = OnDemand)]</c>.
    /// </summary>
    /// <param name="patterns">Tool names or globs (<c>*</c>, <c>?</c>), matched case-sensitively.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="ArgumentException">A pattern is null, empty or white space.</exception>
    public ToolListingOptions Listed(params string[] patterns)
    {
        Add(_listed, patterns);
        return this;
    }

    private static void Add(List<string> target, string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("A tool listing pattern must not be null, empty or white space.", nameof(patterns));
            }

            target.Add(pattern);
        }
    }
}
