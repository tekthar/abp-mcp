using System.Net;
using System.Text;
using FluentAssertions;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// The MCP transport spec requires a server to refuse a request whose Origin header is present and not
/// allowed (403), so a web page cannot drive a local MCP server through the user's browser.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class OriginValidationTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""";

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]                       // desktop agents, CLIs: no Origin
    [InlineData("http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost", HttpStatusCode.Forbidden)]  // same host is not trusted (DNS rebinding)
    [InlineData("null", HttpStatusCode.Forbidden)]              // sandboxed iframe / file://
    public async Task McpEndpoint_EnforcesOrigin(string? origin, HttpStatusCode expected)
    {
        await using var host = await SampleHost.StartAsync();

        var response = await PostInitializeAsync(host, origin);

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("http://localhost:6274")]
    [InlineData("http://localhost:6274/")]
    public async Task McpEndpoint_AcceptsAllowedOrigin(string origin)
    {
        await using var host = await SampleHost.StartAsync(o => o.AllowedOrigins.Add("http://localhost:6274"));

        (await PostInitializeAsync(host, origin)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wildcard_AcceptsAnyOrigin()
    {
        await using var host = await SampleHost.StartAsync(o => o.AllowedOrigins.Add("*"));

        (await PostInitializeAsync(host, "http://anything.example")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/mcp/_discover")]
    [InlineData("/mcp/_explain")]
    public async Task DiagnosticsEndpoints_EnforceOrigin(string path)
    {
        await using var host = await SampleHost.StartAsync();
        using var http = host.Server.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "http://evil.example");

        (await http.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpResponseMessage> PostInitializeAsync(SampleHost host, string? origin)
    {
        using var http = host.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await http.SendAsync(request);
    }
}
