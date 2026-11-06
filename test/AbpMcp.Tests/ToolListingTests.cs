using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Attributes;
using AbpMcp.Listing;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Tests;

/// <summary>
/// #119: which tools are listed in <c>tools/list</c> and which are on demand. Precedence, highest first: a host pin
/// (<c>ToolListing.Listed</c>), a host OnDemand pattern, <c>[McpTool(Listing = ...)]</c> (method, else class) or an
/// add-in's descriptor value, the default (listed).
/// </summary>
public class ToolListingResolverTests
{
    [Fact]
    public void Default_IsListed()
    {
        var decision = Resolve(Tool("Plain_Get"));

        decision.Listing.Should().Be(McpToolListing.Listed);
        decision.Reason.Should().StartWith("Listed: the default.");
    }

    [Fact]
    public void AttributeOnMethod_MakesItOnDemand_AndTheReasonNamesTheMethod()
    {
        var decision = Resolve(Declared<MixedAppService>(nameof(MixedAppService.PurgeAsync)));

        decision.Listing.Should().Be(McpToolListing.OnDemand);
        decision.Reason.Should().Be("On demand: [McpTool(Listing = McpToolListing.OnDemand)] on method MixedAppService.PurgeAsync.");
    }

    [Fact]
    public void AttributeOnClass_MakesEveryMethodOnDemand_AndTheReasonNamesTheClass()
    {
        var decision = Resolve(Declared<ArchiveAppService>(nameof(ArchiveAppService.ExportAsync)));

        decision.Listing.Should().Be(McpToolListing.OnDemand);
        decision.Reason.Should().Be("On demand: [McpTool(Listing = McpToolListing.OnDemand)] on class ArchiveAppService.");
    }

    [Fact]
    public void McpToolOnMethod_ReplacesTheClassAttribute_ListingIncluded()
    {
        // [McpTool] on the method wins over the class's [McpTool(Listing = OnDemand)], as for Name and Description.
        var decision = Resolve(Declared<ArchiveAppService>(nameof(ArchiveAppService.GetStatusAsync)));

        decision.Listing.Should().Be(McpToolListing.Listed);
    }

    [Fact]
    public void HostOnDemandPattern_WinsOverTheAttribute()
    {
        var decision = Resolve(Declared<MixedAppService>(nameof(MixedAppService.GetAsync)), o => o.ToolListing.OnDemand("Mixed_*"));

        decision.Listing.Should().Be(McpToolListing.OnDemand);
        decision.Reason.Should().Contain("AbpMcpOptions.ToolListing.OnDemand(\"Mixed_*\")");
    }

    [Fact]
    public void HostPin_WinsOverOnDemandPatternsAndTheAttribute()
    {
        var options = (Action<AbpMcpOptions>)(o => o.ToolListing.OnDemand("*").Listed("Mixed_Purge"));

        var pinned = Resolve(Declared<MixedAppService>(nameof(MixedAppService.PurgeAsync)), options);
        var other = Resolve(Declared<MixedAppService>(nameof(MixedAppService.GetAsync)), options);

        pinned.Listing.Should().Be(McpToolListing.Listed);
        pinned.Reason.Should().Contain("AbpMcpOptions.ToolListing.Listed(\"Mixed_Purge\") pins it");
        other.Listing.Should().Be(McpToolListing.OnDemand);
    }

    [Fact]
    public void AddinDeclaredOnDemand_IsKept_AndTheReasonSaysSo()
    {
        var decision = Resolve(Tool("Insights_Report") with { Listing = McpToolListing.OnDemand });

        decision.Listing.Should().Be(McpToolListing.OnDemand);
        decision.Reason.Should().Contain("by the add-in");
    }

    [Fact]
    public void Builder_CopiesTheAttributesListing()
    {
        var method = typeof(MixedAppService).GetMethod(nameof(MixedAppService.PurgeAsync))!;
        var expose = method.GetCustomAttribute<McpToolAttribute>()!;

        var descriptor = new ToolDescriptorBuilder().Build(
            typeof(MixedAppService), method, new ActionApiDescriptionModel { Name = method.Name, HttpMethod = "DELETE" }, expose, new AbpMcpOptions());

        descriptor.Listing.Should().Be(McpToolListing.OnDemand);
    }

    [Fact]
    public void AttributeDefault_IsListed() => new McpToolAttribute().Listing.Should().Be(McpToolListing.Listed);

    private static ToolListingDecision Resolve(ToolDescriptor tool, Action<AbpMcpOptions>? configure = null)
    {
        var options = new AbpMcpOptions();
        configure?.Invoke(options);
        return new ToolListingResolver(Options.Create(options)).Resolve(tool);
    }

    /// <summary>A descriptor as the builder makes it: the listing declared by the effective [McpTool].</summary>
    private static ToolDescriptor Declared<TService>(string methodName)
    {
        var method = typeof(TService).GetMethod(methodName)!;
        var expose = method.GetCustomAttribute<McpToolAttribute>() ?? typeof(TService).GetCustomAttribute<McpToolAttribute>()!;
        var service = typeof(TService).Name.Replace("AppService", string.Empty, StringComparison.Ordinal);
        return Tool($"{service}_{methodName.Replace("Async", string.Empty, StringComparison.Ordinal)}") with
        {
            ServiceType = typeof(TService),
            Method = method,
            Listing = expose.Listing,
        };
    }

    internal static ToolDescriptor Tool(string name) => new()
    {
        Name = name,
        Description = name,
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = [],
        RequiredPermissions = [],
    };

    [McpTool]
    internal class MixedAppService
    {
        public Task GetAsync() => Task.CompletedTask;

        [McpTool(Listing = McpToolListing.OnDemand)]
        public Task PurgeAsync() => Task.CompletedTask;
    }

    [McpTool(Listing = McpToolListing.OnDemand)]
    internal class ArchiveAppService
    {
        public Task ExportAsync() => Task.CompletedTask;

        [McpTool]
        public Task GetStatusAsync() => Task.CompletedTask;
    }
}

public class ToolNameGlobTests
{
    [Theory]
    [InlineData("Jira*", "Jira_GetIssue", true)]
    [InlineData("Jira*", "Jira", true)]
    [InlineData("Jira*", "jira_GetIssue", false)]   // case-sensitive, like tool names
    [InlineData("*_Delete", "Member_Delete", true)]
    [InlineData("*_Delete", "Member_DeleteAll", false)] // the whole name must match
    [InlineData("Member_?et", "Member_Get", true)]
    [InlineData("Member_?et", "Member_Gett", false)]
    [InlineData("*", "Anything_At_All", true)]
    [InlineData("A*B*C", "AxxBxxC", true)]
    [InlineData("A*B*C", "AxxBxxD", false)]
    [InlineData("Loan_CheckOut", "Loan_CheckOut", true)]
    [InlineData("Loan_CheckOut", "Loan_CheckOutNow", false)]
    public void Matches(string pattern, string name, bool expected) =>
        ToolNameGlob.IsMatch(pattern, name).Should().Be(expected);
}

public class ToolListingOptionsTests
{
    [Fact]
    public void Patterns_AreKeptInOrder_AndCallsChain()
    {
        var options = new AbpMcpOptions();

        options.ToolListing.OnDemand("Jira*", "*_Delete").Listed("Jira_GetIssue").OnDemand("Confluence*");

        options.ToolListing.OnDemandPatterns.Should().Equal("Jira*", "*_Delete", "Confluence*");
        options.ToolListing.ListedPatterns.Should().Equal("Jira_GetIssue");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void BlankPattern_IsRefused(string? pattern)
    {
        var options = new AbpMcpOptions();

        var act = () => options.ToolListing.OnDemand(pattern!);

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>The registry resolves every tool's listing once per build, after the add-ins, and stores it on the descriptor.</summary>
public class ToolRegistryListingTests
{
    [Fact]
    public void Initialize_StoresTheEffectiveListingAndItsReason_OnEachDescriptor()
    {
        var registry = NewRegistry([ToolListingResolverTests.Tool("Jira_Delete"), ToolListingResolverTests.Tool("Jira_Get")],
            o => o.ToolListing.OnDemand("Jira_*").Listed("Jira_Get"));

        registry.Initialize();

        registry.TryGetByName("Jira_Delete", out var onDemand).Should().BeTrue();
        onDemand!.Listing.Should().Be(McpToolListing.OnDemand);
        onDemand.ListingReason.Should().Contain("OnDemand(\"Jira_*\")");
        registry.Tools.Single(t => t.Name == "Jira_Get").Listing.Should().Be(McpToolListing.Listed);
    }

    [Fact]
    public void HostPatterns_ApplyToAddinTools()
    {
        var registry = NewRegistry([ToolListingResolverTests.Tool("Plain_Get")], o => o.ToolListing.OnDemand("Insights_*"),
            new ContributingAddin(ToolListingResolverTests.Tool("Insights_Report")));

        registry.Initialize();

        registry.Tools.Single(t => t.Name == "Insights_Report").Listing.Should().Be(McpToolListing.OnDemand);
    }

    [Theory]
    [InlineData("mcp_search_tools")]
    [InlineData("mcp_describe_tools")]
    [InlineData("mcp_call_tool")]
    [InlineData("mcp_call_tools")]
    public void ToolNamedLikeAGatewayTool_FailsStartup(string name)
    {
        var registry = NewRegistry([ToolListingResolverTests.Tool(name)]);

        var act = () => registry.Initialize();

        act.Should().Throw<AbpMcpConfigurationException>().WithMessage($"*'{name}' is reserved for abp-mcp's gateway tools*");
    }

    private static DynamicMcpToolRegistry NewRegistry(
        IReadOnlyList<ToolDescriptor> tools,
        Action<AbpMcpOptions>? configure = null,
        params IAbpMcpAddin[] addins)
    {
        var value = new AbpMcpOptions { RequireAtLeastOneTool = false };
        configure?.Invoke(value);
        var options = Options.Create(value);
        return new DynamicMcpToolRegistry(
            new StaticReader(tools),
            addins,
            new ToolListingResolver(options),
            new EmptyServiceProvider(),
            options,
            NullLogger<DynamicMcpToolRegistry>.Instance);
    }

    private sealed class StaticReader(IReadOnlyList<ToolDescriptor> tools) : IApiDefinitionReader
    {
        public IReadOnlyList<ToolDescriptor> Read() => tools;
    }

    private sealed class ContributingAddin(ToolDescriptor tool) : IAbpMcpAddin
    {
        public int Order => 50;

        public void Contribute(AbpMcpToolBuildContext context) => context.AddTool(tool);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
