using AbpMcp.Metadata;
using Microsoft.AspNetCore.Http;

namespace AbpMcp.Registration;

/// <summary>
/// The per-caller gate on which tools exist for the caller: not disabled by the kill switch
/// (<see cref="Settings.IDisabledToolsProvider"/>) and passed by <see cref="Authorization.IAbpMcpToolAuthorizer"/>.
/// <c>tools/list</c>, <c>mcp_search_tools</c> and <c>mcp_describe_tools</c> all filter through it, so a tool the caller
/// cannot use is never listed, found or described (design rule 3).
/// </summary>
internal interface IAbpMcpToolVisibility
{
    /// <summary>The tools of <paramref name="tools"/> the caller of <paramref name="httpContext"/> may see, in their original order.</summary>
    /// <param name="tools">The candidates: registry tools, gateway tools, or search matches.</param>
    /// <param name="httpContext">The current request. Null means no caller; any tool with requirements is then hidden.</param>
    /// <param name="cancellationToken">Cancellation signal.</param>
    Task<IReadOnlyList<ToolDescriptor>> FilterAsync(
        IEnumerable<ToolDescriptor> tools,
        HttpContext? httpContext,
        CancellationToken cancellationToken);
}
