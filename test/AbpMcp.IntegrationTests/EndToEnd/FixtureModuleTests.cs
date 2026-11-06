using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Fixtures.Application;
using AbpMcp.Fixtures.HttpApi;
using AbpMcp.Gateway;
using FluentAssertions;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// The Library sample with the layered fixture modules plugged in: an *.Application assembly that tags its
/// services through AbpMcp.Abstractions only (#98), and an *.HttpApi assembly with a hand-written controller
/// serving a <c>[RemoteService(IsEnabled = false)]</c> app service (#101).
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class FixtureModuleTests : IAsyncLifetime
{
    private const string ApplicationAssemblyName = "AbpMcp.Fixtures.Application";

    private SampleHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await SampleHost.StartAsync(plugInModules: typeof(FixturesHttpApiModule));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void ApplicationFixture_ReferencesAbstractionsOnly()
    {
        var references = typeof(QuoteAppService).Assembly.GetReferencedAssemblies().Select(r => r.Name).ToList();

        references.Should().Contain("AbpMcp.Abstractions");
        references.Should().NotContain(["AbpMcp", "Microsoft.AspNetCore.Mvc.Core", "ModelContextProtocol.Core"]);
    }

    [Fact]
    public async Task AbstractionsOnlyAssembly_IsAutoDiscovered_AndItsToolsAreListedAndCallable()
    {
        await using var client = await _host.ConnectAsync();

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(["Quote_GetOfTheDay", "Member_List"]);
        Names((await ExplainAsync())["auto_discovered_assemblies"]).Should().Contain(ApplicationAssemblyName);

        var result = await client.CallToolAsync("Quote_GetOfTheDay");
        result.IsError.Should().NotBe(true);
        result.StructuredContent!.Value.GetProperty("author").GetString().Should().Be("George R. R. Martin");
    }

    // #119: [McpTool(Listing = McpToolListing.OnDemand)] in an assembly that references AbpMcp.Abstractions only.
    [Fact]
    public async Task OnDemandAttribute_KeepsTheToolOutOfToolsList_AndTheGatewayReachesIt()
    {
        await using var client = await _host.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        var search = await OnDemandToolsTests.GatewayAsync(client, AbpMcpGatewayToolNames.Search, new { query = "proverb" });
        var call = await client.CallToolAsync(AbpMcpGatewayToolNames.Call, new Dictionary<string, object?> { ["name"] = "Proverb_GetRandom" });
        var row = Decisions(await ExplainAsync("?service=Proverb")).Single();

        names.Should().NotContain("Proverb_GetRandom").And.Contain(AbpMcpGatewayToolNames.All);
        search["tools"]![0]!["name"]!.GetValue<string>().Should().Be("Proverb_GetRandom");
        call.IsError.Should().NotBe(true, OnDemandToolsTests.Text(call));
        call.StructuredContent!.Value.GetProperty("text").GetString().Should().Be("Still waters run deep.");
        row["listing"]!.GetValue<string>().Should().Be("on_demand");
        row["listing_reason"]!.GetValue<string>().Should().Be("On demand: [McpTool(Listing = McpToolListing.OnDemand)] on class ProverbAppService.");
    }

    [Fact]
    public async Task HandWrittenControllerService_IsNotATool_AndExplainSaysWhy()
    {
        await using var client = await _host.ConnectAsync();
        (await client.ListToolsAsync()).Select(t => t.Name).Should().NotContain(n => n.StartsWith("Media_", StringComparison.Ordinal));

        var rows = Decisions(await ExplainAsync())
            .Where(d => d["service"]!.GetValue<string>() == typeof(MediaAppService).FullName)
            .ToList();

        rows.Select(d => d["method"]!.GetValue<string>()).Should().BeEquivalentTo("GetAsync", "UploadAsync");
        foreach (var row in rows)
        {
            row["included"]!.GetValue<bool>().Should().BeFalse();
            row["reason"]!.GetValue<string>().Should()
                .Contain("[RemoteService(IsEnabled = false)]")
                .And.Contain("hand-written controller " + typeof(MediaController).FullName)
                .And.Contain("thin, remoted [McpTool] app service")
                .And.Contain("docs/hand-written-controllers.md");
        }
    }

    [Fact]
    public async Task ExplainServiceFilter_AppliesToUndescribedServices()
    {
        var filtered = Decisions(await ExplainAsync("?service=Quote")).ToList();

        filtered.Should().NotBeEmpty();
        filtered.Should().OnlyContain(d => d["service"]!.GetValue<string>() == typeof(QuoteAppService).FullName);
        filtered.Should().OnlyContain(d => d["reason"]!.GetValue<string>() == "Included: [McpTool] on class QuoteAppService.");
    }

    [Fact]
    public async Task WrapperAppService_ExposesTheHandWrittenServicesJsonOperations()
    {
        await using var client = await _host.ConnectAsync();
        var id = Guid.NewGuid();

        var result = await client.CallToolAsync("MediaTools_Get", new Dictionary<string, object?> { ["id"] = id });

        result.IsError.Should().NotBe(true);
        result.StructuredContent!.Value.GetProperty("fileName").GetString().Should().Be($"cover-{id:N}.png");
    }

    [Fact]
    public async Task HandWrittenControllerRoute_IsUnchanged()
    {
        using var http = _host.Server.CreateClient();
        var id = Guid.NewGuid();

        var response = await http.GetAsync($"/api/fixture/media/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("fileName").GetString().Should().Be($"cover-{id:N}.png");
    }

    private async Task<JsonNode> ExplainAsync(string query = "")
    {
        using var http = _host.Server.CreateClient();
        return JsonNode.Parse(await http.GetStringAsync("/mcp/_explain" + query))!;
    }

    private static IEnumerable<JsonNode> Decisions(JsonNode explain) => explain["decisions"]!.AsArray().Select(d => d!);

    private static IEnumerable<string> Names(JsonNode? array) =>
        array!.AsArray().Select(n => n!.GetValue<string>());
}
