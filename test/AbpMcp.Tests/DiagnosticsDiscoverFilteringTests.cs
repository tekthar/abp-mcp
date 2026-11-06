using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using AbpMcp.Authorization;
using AbpMcp.Diagnostics;
using AbpMcp.Gateway;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using AbpMcp.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AbpMcp.Tests;

/// <summary>
/// <c>/mcp/_discover</c> lists only the tools the caller passes the authorizer for, so it never reveals
/// a tool's schema or required permissions to a caller <c>tools/list</c> would hide it from.
/// </summary>
public class DiagnosticsDiscoverFilteringTests
{
    [Fact]
    public async Task Discover_ListsOnlyToolsTheCallerIsAuthorizedFor_AndFlagsDisabledOnes()
    {
        var open = Tool("Open_Tool");
        var restricted = Tool("Admin_Purge", "Admin.Purge");
        var disabled = Tool("Open_Disabled");

        var registry = Substitute.For<IDynamicMcpToolRegistry>();
        registry.Tools.Returns([open, restricted, disabled]);

        var authorizer = Substitute.For<IAbpMcpToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<HttpContext?>())
            .Returns(call => call.Arg<ToolDescriptor>()!.RequiredPermissions.Count == 0
                ? AbpMcpToolAuthorizationResult.Success()
                : AbpMcpToolAuthorizationResult.Fail("Missing permission: Admin.Purge"));

        var disabledTools = Substitute.For<IDisabledToolsProvider>();
        disabledTools.GetDisabledToolsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal) { "Open_Disabled" });

        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(authorizer)
            .AddSingleton(disabledTools)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await AbpMcpDiagnosticsEndpoints.HandleDiscover(context);

        context.Response.Body.Position = 0;
        var body = JsonNode.Parse(context.Response.Body)!;
        body["tool_count"]!.GetValue<int>().Should().Be(2);
        var tools = body["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["disabled"]!.GetValue<bool>());
        tools.Should().Equal(new Dictionary<string, bool> { ["Open_Tool"] = false, ["Open_Disabled"] = true });
    }

    // #119: each row says whether the tool is listed or on demand and roughly what it costs in tools/list.
    [Fact]
    public async Task Discover_ReportsListingSchemaTokensAndTheGatewayTools()
    {
        var listed = Tool("Open_Tool");
        var onDemand = Tool("Rare_Tool") with { Listing = McpToolListing.OnDemand, Description = new string('x', 400) };

        var body = await DiscoverAsync(listed, onDemand);

        var rows = body["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        rows["Open_Tool"]!["listing"]!.GetValue<string>().Should().Be("listed");
        rows["Rare_Tool"]!["listing"]!.GetValue<string>().Should().Be("on_demand");
        var rareTokens = rows["Rare_Tool"]!["schema_tokens"]!.GetValue<int>();
        rareTokens.Should().Be((400 + onDemand.InputSchema.ToJsonString().Length + 3) / 4, "description + schemas, in characters / 4");

        body["gateway_tools"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(AbpMcpGatewayToolNames.All);
        body["schema_tokens"]!["on_demand"]!.GetValue<int>().Should().Be(rareTokens);
        body["schema_tokens"]!["listed"]!.GetValue<int>().Should().Be(rows["Open_Tool"]!["schema_tokens"]!.GetValue<int>());
        body["schema_tokens"]!["gateway"]!.GetValue<int>().Should().BePositive();
    }

    [Fact]
    public async Task Discover_ListsNoGatewayTools_WhenNothingIsOnDemand()
    {
        var body = await DiscoverAsync(Tool("Open_Tool"));

        body["gateway_tools"]!.AsArray().Should().BeEmpty();
        body["schema_tokens"]!["gateway"]!.GetValue<int>().Should().Be(0);
    }

    private static async Task<JsonNode> DiscoverAsync(params ToolDescriptor[] tools)
    {
        var registry = Substitute.For<IDynamicMcpToolRegistry>();
        registry.Tools.Returns(tools);
        var authorizer = Substitute.For<IAbpMcpToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<HttpContext?>()).Returns(AbpMcpToolAuthorizationResult.Success());
        var disabledTools = Substitute.For<IDisabledToolsProvider>();
        disabledTools.GetDisabledToolsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>(StringComparer.Ordinal));

        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(authorizer)
            .AddSingleton(disabledTools)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await AbpMcpDiagnosticsEndpoints.HandleDiscover(context);

        context.Response.Body.Position = 0;
        return JsonNode.Parse(context.Response.Body)!;
    }

    private static ToolDescriptor Tool(string name, params string[] permissions) => new()
    {
        Name = name,
        Description = name,
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = [],
        RequiredPermissions = permissions,
    };
}
