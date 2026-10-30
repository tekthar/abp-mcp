using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// MCP requires <c>outputSchema</c> and <c>structuredContent</c> to be objects. Regression for
/// <c>tools/list</c> throwing (and so advertising nothing) as soon as one tool returned a list,
/// because the SDK rejects an <c>{"type":"array"}</c> output schema.
/// </summary>
public sealed class StructuredOutputTests : AbpMcpIntegrationTestBase
{
    [Fact]
    public async Task ToolsList_SucceedsAndWrapsListReturnSchema()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var tools = await new McpHandlers(services).ListAsync();

            var list = tools.Single(t => t.Name == "Book_GetList");
            var schema = list.OutputSchema!.Value;
            schema.GetProperty("type").GetString().Should().Be("object");
            schema.GetProperty("required")[0].GetString().Should().Be("result");
            schema.GetProperty("properties").GetProperty("result").GetProperty("type").GetString().Should().Be("array");

            // An object-returning tool is advertised unchanged.
            var create = tools.Single(t => t.Name == "Book_Create");
            create.OutputSchema!.Value.GetProperty("properties").TryGetProperty("result", out _).Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task ToolsCall_WrapsListResultToMatchSchema()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var mcp = new McpHandlers(services);
            var title = JsonSerializer.SerializeToElement(new { title = "Dune", author = "Herbert", year = 1965 });
            (await mcp.CallAsync("Book_Create", new Dictionary<string, JsonElement> { ["input"] = title })).IsError.Should().BeFalse();

            var result = await mcp.CallAsync("Book_GetList");

            result.IsError.Should().BeFalse();
            var items = result.StructuredContent!.Value.GetProperty("result");
            items.ValueKind.Should().Be(JsonValueKind.Array);
            items.GetArrayLength().Should().Be(1);
            return true;
        });
    }
}
