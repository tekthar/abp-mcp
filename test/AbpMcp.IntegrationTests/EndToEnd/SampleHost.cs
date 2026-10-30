using AbpMcp.Sample;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// The bundled Library sample, booted exactly as its <c>Program.cs</c> boots it but on an in-memory
/// <see cref="TestServer"/>. Tests talk to it over real HTTP with the MCP SDK's own client, so every
/// layer an agent goes through (transport, SDK handlers, wiring, dispatcher) is exercised.
/// </summary>
public sealed class SampleHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleHost(WebApplication app)
    {
        _app = app;
        Server = app.GetTestServer();
    }

    /// <summary>The in-memory server hosting the sample.</summary>
    public TestServer Server { get; }

    /// <summary>The root service provider of the running host.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Boot the sample. <paramref name="configure"/> runs after the sample module's own options.</summary>
    public static async Task<SampleHost> StartAsync(Action<AbpMcpOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();

        await builder.Services.AddApplicationAsync<LibrarySampleModule>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        var app = builder.Build();
        await app.InitializeApplicationAsync();
        await app.StartAsync();
        return new SampleHost(app);
    }

    /// <summary>Connect an MCP client to <c>/mcp</c> over the in-memory server (streamable HTTP).</summary>
    public Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            Server.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return McpClient.CreateAsync(transport);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
