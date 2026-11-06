using System.Net;
using System.Text;
using FluentAssertions;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// <see cref="AbpMcpOptions.Stateless"/> lets several instances share one load balancer without session
/// affinity (#100): any instance answers any request, with no <c>initialize</c> and no session id to lose.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class StatelessTransportTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";

    [Fact]
    public async Task Stateless_ToolsListWithoutInitialize_IsAnswered_WithoutASessionId()
    {
        await using var host = await SampleHost.StartAsync(o => o.Stateless = true);

        using var response = await PostToolsListAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse(because: "a stateless server mints no session");
        body.Should().Contain("\"tools\"").And.Contain("Catalog_", because: "the sample's tools are listed");
    }

    [Fact]
    public async Task Stateless_SdkClient_ListsAndCallsTools()
    {
        await using var host = await SampleHost.StartAsync(o => o.Stateless = true);
        await using var client = await host.ConnectAsync();

        (await client.ListToolsAsync()).Should().Contain(t => t.Name == "Member_List");
        var result = await client.CallToolAsync("Member_List");

        result.IsError.Should().NotBe(true);
        client.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task Stateful_Default_RequiresASession()
    {
        // The contrast: by default the transport is stateful, so the same request is refused until the
        // client has initialized a session (which is what breaks behind a load balancer without affinity).
        await using var host = await SampleHost.StartAsync();

        using var response = await PostToolsListAsync(host);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> PostToolsListAsync(SampleHost host)
    {
        using var http = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(ToolsList, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        return await http.SendAsync(request);
    }
}
