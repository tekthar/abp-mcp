using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Regression for #13 Stage A: a tool added to <c>DisabledTools</c> at runtime through
/// <see cref="IOptionsMonitor{TOptions}"/> must drop out of <c>tools/list</c>, not only be refused on
/// call, and <c>/mcp/_discover</c> must flag it. Both read <see cref="IOptions{TOptions}"/>, a
/// separately cached instance, so the tool kept being advertised to agents that would then get
/// <c>DISABLED</c> for calling it.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class DisabledToolsHotReloadTests : IAsyncLifetime
{
    private const string Tool = "Member_List";

    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ToolsList_DropsAndRestoresToolToggledAtRuntime()
    {
        await using var client = await _host.ConnectAsync();
        var disabled = _host.Services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>().CurrentValue.DisabledTools;

        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(Tool);

        disabled.Add(Tool);
        (await client.ListToolsAsync()).Select(t => t.Name).Should().NotContain(Tool);

        disabled.Remove(Tool);
        (await client.ListToolsAsync()).Select(t => t.Name).Should().Contain(Tool);
    }

    [Fact]
    public async Task Discover_FlagsToolDisabledAtRuntime()
    {
        using var http = _host.Server.CreateClient();
        _host.Services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>().CurrentValue.DisabledTools.Add(Tool);

        var body = JsonNode.Parse(await http.GetStringAsync("/mcp/_discover"))!;

        var row = body["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == Tool)!;
        row["disabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ToolsCall_RefusesToolDisabledAtRuntime()
    {
        await using var client = await _host.ConnectAsync();
        _host.Services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>().CurrentValue.DisabledTools.Add(Tool);

        var result = await client.CallToolAsync(Tool);

        result.IsError.Should().BeTrue();
        ((TextContentBlock)result.Content[0]).Text.Should().StartWith("[DISABLED]");
    }
}
