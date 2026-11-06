using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Modularity;

namespace AbpMcp.Tests;

/// <summary>
/// #119: <c>AbpMcpOptions.ToolListing</c> configured in a module's <c>PreConfigureServices</c>, before
/// <see cref="AbpMcpModule"/> registers its services, survives that registration (CLAUDE.md design rule 6).
/// </summary>
public class ToolListingPreConfigureTests
{
    [Fact]
    public async Task ToolListing_ConfiguredInPreConfigureServices_IsKept()
    {
        var services = new ServiceCollection();
        using var application = await AbpApplicationFactory.CreateAsync<PreConfiguringModule>(services);
        await using var provider = services.BuildServiceProvider();

        var listing = provider.GetRequiredService<IOptions<AbpMcpOptions>>().Value.ToolListing;

        listing.OnDemandPatterns.Should().Equal("Jira*", "*_Delete");
        listing.ListedPatterns.Should().Equal("Jira_GetIssue");
    }

    [DependsOn(typeof(AbpMcpModule))]
    public sealed class PreConfiguringModule : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context) =>
            Configure<AbpMcpOptions>(o => o.ToolListing.OnDemand("Jira*", "*_Delete").Listed("Jira_GetIssue"));
    }
}
