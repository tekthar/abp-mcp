using AbpMcp.Attributes;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Fixtures.Application;

/// <summary>A proverb.</summary>
public sealed class ProverbDto
{
    public string Text { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;
}

public interface IProverbAppService : IApplicationService
{
    Task<ProverbDto> GetRandomAsync();
}

/// <summary>
/// A rarely used service kept out of <c>tools/list</c> by its own attribute: an agent finds <c>Proverb_GetRandom</c>
/// through <c>mcp_search_tools</c> (#119). <c>McpToolListing</c> comes from AbpMcp.Abstractions, so tagging a tool on
/// demand still needs no reference to AbpMcp.
/// </summary>
[McpTool(Listing = McpToolListing.OnDemand)]
public class ProverbAppService : IProverbAppService, ITransientDependency
{
    public Task<ProverbDto> GetRandomAsync() =>
        Task.FromResult(new ProverbDto { Text = "Still waters run deep.", Origin = "English" });
}
