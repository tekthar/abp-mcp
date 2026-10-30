using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// tools/list and tools/call through the MCP SDK's client against the running Library sample.
/// Regression tier for failures the descriptor-level tests cannot see (#49, #54).
/// </summary>
public sealed class McpProtocolTests : IAsyncLifetime
{
    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ToolsList_Succeeds_AndEveryOutputSchemaIsAnObject()
    {
        await using var client = await _host.ConnectAsync();

        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).Should().Contain(["Member_List", "Member_Get", "Loan_CheckOut", "Library_Overview"]);
        foreach (var tool in tools.Where(t => t.ProtocolTool.OutputSchema is not null))
        {
            tool.ProtocolTool.OutputSchema!.Value.GetProperty("type").GetString()
                .Should().Be("object", $"MCP requires an object output schema ({tool.Name})");
        }
    }

    [Fact]
    public async Task ToolsList_AdvertisesCollectionResultWrappedUnderResult()
    {
        await using var client = await _host.ConnectAsync();

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "Member_List");

        var schema = tool.ProtocolTool.OutputSchema!.Value;
        schema.GetProperty("properties").GetProperty("result").GetProperty("type").GetString().Should().Be("array");
    }

    [Fact]
    public async Task ToolsCall_CollectionResult_StructuredContentMatchesAdvertisedSchema()
    {
        await using var client = await _host.ConnectAsync();

        var result = await client.CallToolAsync("Member_List");

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        result.StructuredContent.Value.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ToolsCall_ObjectResult_StructuredContentIsTheDtoItself()
    {
        await using var client = await _host.ConnectAsync();

        var result = await client.CallToolAsync("Member_Register", new Dictionary<string, object?>
        {
            ["input"] = new { name = "Ada Lovelace", email = "ada@example.org" },
        });

        result.IsError.Should().NotBe(true, ((TextContentBlock)result.Content[0]).Text);
        result.StructuredContent!.Value.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("Member_Get", """{"id":"not-a-guid"}""", "Parameter 'id'")]
    [InlineData("Loan_CheckOut", """{"input":"x"}""", "Parameter 'input'")]
    [InlineData("Member_Register", """{"input":{"name":42}}""", "$.name")]
    public async Task ToolsCall_MalformedArgument_ReturnsValidationError(string tool, string arguments, string expected)
    {
        await using var client = await _host.ConnectAsync();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!;

        var result = await client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = args });

        result.IsError.Should().BeTrue();
        var text = ((TextContentBlock)result.Content[0]).Text;
        text.Should().StartWith("[VALIDATION_ERROR]").And.Contain(expected);
    }
}
