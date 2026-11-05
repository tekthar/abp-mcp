using System.Net;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace AbpMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Regression for #84: one tool bound to a permission the host never defined (a typo, a permission provider
/// not loaded in this host, a removed module) made ASP.NET Core throw "No policy found", which failed
/// <c>tools/list</c> and <c>/mcp/_discover</c> as a whole, for every caller. That tool must now be refused like
/// any permission the caller lacks, and every other tool must keep working.
/// </summary>
[Collection(SampleHostCollection.Name)]
public sealed class UndefinedPermissionTests : IAsyncLifetime
{
    private const string BrokenTool = "Broken_Tool";
    private const string UndefinedPermission = "Nope.UndefinedPermission";

    private SampleHost _host = null!;

    public async Task InitializeAsync() => _host = await SampleHost.StartAsync(configureBuilder: builder =>
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAbpMcpAddin, BrokenToolAddin>()));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ToolsList_StillListsEveryOtherTool()
    {
        await using var client = await _host.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();

        names.Should().Contain(["Member_List", "Loan_CheckOut", "Library_Overview"]).And.NotContain(BrokenTool);
    }

    [Fact]
    public async Task Discover_Succeeds_WithoutTheTool()
    {
        using var http = _host.Server.CreateClient();

        var response = await http.GetAsync("/mcp/_discover");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tools = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["tools"]!.AsArray();
        tools.Select(t => t!["name"]!.GetValue<string>()).Should().Contain("Member_List").And.NotContain(BrokenTool);
    }

    [Fact]
    public async Task ToolsCall_IsRefusedAsForbidden()
    {
        await using var client = await _host.ConnectAsync();

        var result = await client.CallToolAsync(BrokenTool);

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().StartWith($"[{AbpMcpErrorCodes.Forbidden}]").And.Contain(UndefinedPermission);
    }

    [Fact]
    public async Task Explain_SaysThePermissionIsNotDefined()
    {
        using var http = _host.Server.CreateClient();

        var body = JsonNode.Parse(await http.GetStringAsync("/mcp/_explain"))!;

        body["undefined_permissions"]!.AsArray().Select(t => (t!["tool"]!.GetValue<string>(), t["permission"]!.GetValue<string>()))
            .Should().Equal((BrokenTool, UndefinedPermission));
    }

    /// <summary>A dynamic tool bound to a permission nothing defines.</summary>
    private sealed class BrokenToolAddin : IAbpMcpAddin
    {
        public int Order => 50;

        public void Contribute(AbpMcpToolBuildContext context) => context.AddTool(new ToolDescriptor
        {
            Name = BrokenTool,
            Description = "Bound to a permission the host never defined.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = [UndefinedPermission],
            Handler = (_, _) => throw new InvalidOperationException("must never run"),
        });
    }
}
