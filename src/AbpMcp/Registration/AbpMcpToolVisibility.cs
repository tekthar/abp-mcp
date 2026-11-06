using AbpMcp.Authorization;
using AbpMcp.Metadata;
using AbpMcp.Settings;
using Microsoft.AspNetCore.Http;

namespace AbpMcp.Registration;

/// <summary>
/// Default <see cref="IAbpMcpToolVisibility"/>: the kill switch first (cheap, one cached read per request), then the
/// authorizer the dispatcher also applies before every invoke, so list, search, describe and call cannot disagree.
/// </summary>
internal sealed class AbpMcpToolVisibility : IAbpMcpToolVisibility
{
    private readonly IAbpMcpToolAuthorizer _authorizer;
    private readonly IDisabledToolsProvider _disabledTools;

    public AbpMcpToolVisibility(IAbpMcpToolAuthorizer authorizer, IDisabledToolsProvider disabledTools)
    {
        _authorizer = authorizer;
        _disabledTools = disabledTools;
    }

    public async Task<IReadOnlyList<ToolDescriptor>> FilterAsync(
        IEnumerable<ToolDescriptor> tools,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var disabled = await _disabledTools.GetDisabledToolsAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<ToolDescriptor>();
        foreach (var tool in tools)
        {
            if (disabled.Contains(tool.Name))
            {
                continue;
            }

            var authorized = await _authorizer.AuthorizeAsync(tool, httpContext).ConfigureAwait(false);
            if (authorized.Succeeded)
            {
                visible.Add(tool);
            }
        }

        return visible;
    }
}
