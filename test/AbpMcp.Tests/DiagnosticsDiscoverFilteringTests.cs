using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Authorization;
using AbpMcp.Diagnostics;
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

    private static ToolDescriptor Tool(string name, params string[] permissions) => new()
    {
        Name = name,
        Description = name,
        InputSchema = new JsonObject { ["type"] = "object" },
        ParameterNames = [],
        RequiredPermissions = permissions,
    };
}
