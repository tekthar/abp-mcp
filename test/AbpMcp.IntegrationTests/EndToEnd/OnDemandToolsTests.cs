using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Gateway;
using AbpMcp.Sample;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// #119: on-demand tools and the gateway tools, end to end through the MCP SDK's client. The sample's catalog tools and
/// <c>Member_Delete</c> are made on demand by host patterns; the anonymous caller lacks <c>Library.Members.Delete</c>.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class OnDemandToolsTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await SampleHost.StartAsync(o => o.ToolListing.OnDemand("Catalog_*", "Member_Delete"));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ToolsList_OmitsOnDemandTools_AndListsTheGatewayTools()
    {
        await using var client = await _host.ConnectAsync();

        var tools = await client.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        names.Should().NotContain(n => n.StartsWith("Catalog_", StringComparison.Ordinal));
        names.Should().Contain(["Member_List", "Loan_CheckOut", "Library_Overview"]).And.Contain(AbpMcpGatewayToolNames.All);
        foreach (var gateway in tools.Where(t => AbpMcpGatewayToolNames.IsGatewayTool(t.Name)))
        {
            gateway.ProtocolTool.OutputSchema.Should().BeNull("gateway tools stay small: their descriptions state the result shape");
            gateway.ProtocolTool.InputSchema.GetProperty("type").GetString().Should().Be("object");
        }
    }

    [Fact]
    public async Task Search_FindsOnDemandTools_WithOneLineSummaries()
    {
        await using var client = await _host.ConnectAsync();

        var add = await GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "add edition" });
        var get = await GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "get title" });

        var edition = add["tools"]![0]!;
        edition["name"]!.GetValue<string>().Should().Be("Catalog_AddEdition");
        edition["listing"]!.GetValue<string>().Should().Be("on_demand");
        edition["http_method"]!.GetValue<string>().Should().Be("POST");
        edition["read_only"]!.GetValue<bool>().Should().BeFalse();
        edition["description"]!.GetValue<string>().Should().Be("Add a new edition (a specific format and ISBN) to an existing title.");

        var title = get["tools"]![0]!;
        title["name"]!.GetValue<string>().Should().Be("Catalog_GetTitle");
        title["http_method"]!.GetValue<string>().Should().Be("GET");
        title["read_only"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Search_AlsoCoversListedTools()
    {
        await using var client = await _host.ConnectAsync();

        var result = await GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "overdue loans" });

        var first = result["tools"]![0]!;
        first["name"]!.GetValue<string>().Should().Be("Loan_ListOverdue");
        first["listing"]!.GetValue<string>().Should().Be("listed");
    }

    [Fact]
    public async Task Describe_ReturnsTheSchemasAsToolsListAdvertisesThem()
    {
        await using var client = await _host.ConnectAsync();
        var listed = (await client.ListToolsAsync()).Single(t => t.Name == "Member_List").ProtocolTool;

        var result = await GatewayAsync(client, AbpMcpGatewayToolNames.Describe, new { names = new[] { "Catalog_AddTitle", "Member_List", "No_Such_Tool" } });

        var tools = result["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        tools.Keys.Should().Equal("Catalog_AddTitle", "Member_List");
        result["not_found"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("No_Such_Tool");

        // A listed tool describes exactly as tools/list advertises it.
        JsonNode.DeepEquals(tools["Member_List"]!["inputSchema"], JsonNode.Parse(listed.InputSchema.GetRawText())).Should().BeTrue();
        JsonNode.DeepEquals(tools["Member_List"]!["outputSchema"], JsonNode.Parse(listed.OutputSchema!.Value.GetRawText())).Should().BeTrue();
        tools["Member_List"]!["description"]!.GetValue<string>().Should().Be(listed.Description);

        // An on-demand tool describes as _discover reports it.
        var row = (await DiscoverAsync())["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "Catalog_AddTitle")!;
        JsonNode.DeepEquals(tools["Catalog_AddTitle"]!["inputSchema"], row["input_schema"]).Should().BeTrue();
        JsonNode.DeepEquals(tools["Catalog_AddTitle"]!["outputSchema"], row["output_schema"]).Should().BeTrue();
    }

    [Fact]
    public async Task CallTool_RunsAnOnDemandTool_ThroughTheDispatcher()
    {
        await using var client = await _host.ConnectAsync();
        var name = $"Gateway Title {Guid.NewGuid():N}";

        var result = await client.CallToolAsync(AbpMcpGatewayToolNames.Call, new Dictionary<string, object?>
        {
            ["name"] = "Catalog_AddTitle",
            ["arguments"] = new { input = NewTitle(name) },
        });

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("name").GetString().Should().Be(name);
        TitleExists(name).Should().BeTrue();
    }

    [Fact]
    public async Task DirectToolsCall_ToAnOnDemandTool_StillWorks()
    {
        await using var client = await _host.ConnectAsync();

        var result = await client.CallToolAsync("Catalog_SearchTitles", new Dictionary<string, object?> { ["filter"] = new { } });

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("result").GetArrayLength().Should().BePositive();
    }

    [Fact]
    public async Task CallTools_Sequential_StopsAtTheFirstError()
    {
        await using var client = await _host.ConnectAsync();
        var (first, third) = ($"Seq One {Guid.NewGuid():N}", $"Seq Three {Guid.NewGuid():N}");

        var result = await GatewayAsync(client, AbpMcpGatewayToolNames.CallMany, new
        {
            calls = Batch(first, third),
            mode = "sequential",
        });

        Statuses(result).Should().Equal("ok", "error", "skipped");
        result["results"]![1]!["content"]![0]!["text"]!.GetValue<string>().Should().StartWith("[LIBRARY:MEMBER_NOT_FOUND]");
        result["results"]![0]!["structuredContent"]!["name"]!.GetValue<string>().Should().Be(first);
        TitleExists(first).Should().BeTrue();
        TitleExists(third).Should().BeFalse("the call after the error never ran");
    }

    [Fact]
    public async Task CallTools_Parallel_RunsEveryCall()
    {
        await using var client = await _host.ConnectAsync();
        var (first, third) = ($"Par One {Guid.NewGuid():N}", $"Par Three {Guid.NewGuid():N}");

        var result = await GatewayAsync(client, AbpMcpGatewayToolNames.CallMany, new { calls = Batch(first, third), mode = "parallel" });

        Statuses(result).Should().Equal("ok", "error", "ok");
        result["failed"]!.GetValue<int>().Should().Be(1);
        TitleExists(first).Should().BeTrue();
        TitleExists(third).Should().BeTrue();
    }

    [Fact]
    public async Task ToolTheCallerMayNotUse_IsNeitherFoundNorDescribed_AndCallingItIsForbidden()
    {
        await using var client = await _host.ConnectAsync();
        var id = Guid.NewGuid();

        var search = await GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "delete member" });
        var describe = await GatewayAsync(client, AbpMcpGatewayToolNames.Describe, new { names = new[] { "Member_Delete" } });
        var viaGateway = await client.CallToolAsync(AbpMcpGatewayToolNames.Call, new Dictionary<string, object?>
        {
            ["name"] = "Member_Delete",
            ["arguments"] = new { id },
        });
        var direct = await client.CallToolAsync("Member_Delete", new Dictionary<string, object?> { ["id"] = id });

        search["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().NotContain("Member_Delete");
        describe["tools"]!.AsArray().Should().BeEmpty();
        describe["not_found"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Member_Delete");
        Text(viaGateway).Should().StartWith("[FORBIDDEN]");
        Text(direct).Should().StartWith("[FORBIDDEN]");
    }

    [Fact]
    public async Task DisabledOnDemandTool_IsNotFound()
    {
        await using var client = await _host.ConnectAsync();
        _host.Services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>().CurrentValue.DisabledTools.Add("Catalog_GetTitle");

        var search = await GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "get title" });
        var call = await client.CallToolAsync(AbpMcpGatewayToolNames.Call, new Dictionary<string, object?>
        {
            ["name"] = "Catalog_GetTitle",
            ["arguments"] = new { id = Guid.NewGuid() },
        });

        search["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().NotContain("Catalog_GetTitle");
        Text(call).Should().StartWith("[DISABLED]");
    }

    [Fact]
    public async Task KillSwitch_HidesAndRefusesAGatewayTool()
    {
        await using var client = await _host.ConnectAsync();
        _host.Services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>().CurrentValue.DisabledTools.Add(AbpMcpGatewayToolNames.CallMany);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        var call = await client.CallToolAsync(AbpMcpGatewayToolNames.CallMany, new Dictionary<string, object?>
        {
            ["calls"] = new[] { new { name = "Member_List" } },
        });

        names.Should().NotContain(AbpMcpGatewayToolNames.CallMany).And.Contain(AbpMcpGatewayToolNames.Search);
        Text(call).Should().StartWith("[DISABLED]");
    }

    [Fact]
    public async Task GatewayTools_CannotBeCalledThroughTheGateway()
    {
        await using var client = await _host.ConnectAsync();

        var result = await client.CallToolAsync(AbpMcpGatewayToolNames.Call, new Dictionary<string, object?>
        {
            ["name"] = AbpMcpGatewayToolNames.CallMany,
            ["arguments"] = new { calls = new[] { new { name = "Member_List" } } },
        });

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("[VALIDATION_ERROR]").And.Contain("is a gateway tool");
    }

    [Fact]
    public async Task Diagnostics_ReportTheListingAndWhy()
    {
        var discover = await DiscoverAsync();
        using var http = _host.Server.CreateClient();
        var explain = JsonNode.Parse(await http.GetStringAsync("/mcp/_explain"))!;

        var rows = discover["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        rows["Catalog_AddTitle"]!["listing"]!.GetValue<string>().Should().Be("on_demand");
        rows["Member_List"]!["listing"]!.GetValue<string>().Should().Be("listed");
        rows["Catalog_AddTitle"]!["schema_tokens"]!.GetValue<int>().Should().BePositive();
        discover["gateway_tools"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(AbpMcpGatewayToolNames.All);

        var decisions = explain["decisions"]!.AsArray().Where(d => d!["included"]!.GetValue<bool>())
            .ToDictionary(d => d!["tool"]!.GetValue<string>(), d => d!);
        decisions["Catalog_AddTitle"]!["listing"]!.GetValue<string>().Should().Be("on_demand");
        decisions["Catalog_AddTitle"]!["listing_reason"]!.GetValue<string>().Should()
            .Be("On demand: AbpMcpOptions.ToolListing.OnDemand(\"Catalog_*\") matches; the host's patterns win over [McpTool(Listing = ...)].");
        decisions["Member_Delete"]!["listing_reason"]!.GetValue<string>().Should().Contain("OnDemand(\"Member_Delete\")");
        decisions["Member_List"]!["listing"]!.GetValue<string>().Should().Be("listed");
        explain["tool_listing"]!["on_demand"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Catalog_*", "Member_Delete");
        explain["dynamic_tools"]!.AsArray().Single(t => t!["tool"]!.GetValue<string>() == "Library_Overview")!["listing"]!
            .GetValue<string>().Should().Be("listed");
    }

    private static object NewTitle(string name) => new { name, author = "Test Author", firstPublishedYear = 1999, genre = "Test" };

    /// <summary>Add a title, fail on an unknown member, add another title.</summary>
    private static object[] Batch(string first, string third) =>
    [
        new { name = "Catalog_AddTitle", arguments = new { input = NewTitle(first) } },
        new { name = "Member_Get", arguments = new { id = Guid.NewGuid() } },
        new { name = "Catalog_AddTitle", arguments = new { input = NewTitle(third) } },
    ];

    private bool TitleExists(string name)
    {
        using var scope = _host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Titles.Any(t => t.Name == name);
    }

    private async Task<JsonNode> DiscoverAsync()
    {
        using var http = _host.Server.CreateClient();
        return JsonNode.Parse(await http.GetStringAsync("/mcp/_discover"))!;
    }

    /// <summary>Call a gateway tool and return its structured result, checking the text block carries the same JSON.</summary>
    internal static async Task<JsonNode> GatewayAsync(McpClient client, string tool, object arguments)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!;
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = args });

        result.IsError.Should().NotBe(true, Text(result));
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
        JsonNode.DeepEquals(JsonNode.Parse(Text(result)), structured).Should().BeTrue("the text block carries the structured result");
        return structured;
    }

    internal static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static IEnumerable<string> Statuses(JsonNode result) =>
        result["results"]!.AsArray().Select(r => r!["status"]!.GetValue<string>());
}

/// <summary>#119: with no on-demand tool, the gateway is off: nothing extra is listed and its names are unknown.</summary>
[Collection(SampleHostCollection.Name)]
public sealed class GatewayOffTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task WithNothingOnDemand_NoGatewayToolIsListed_OrCallable()
    {
        await using var client = await _host.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        var call = await client.CallToolAsync(AbpMcpGatewayToolNames.Search, new Dictionary<string, object?> { ["query"] = "member" });

        names.Should().NotContain(AbpMcpGatewayToolNames.All).And.Contain("Catalog_AddTitle");
        call.IsError.Should().BeTrue();
        OnDemandToolsTests.Text(call).Should().StartWith("[UNKNOWN_TOOL]");
    }
}
