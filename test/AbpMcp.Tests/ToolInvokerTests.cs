using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Authorization;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using AbpMcp.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AbpMcp.Tests;

/// <summary>
/// #119: the invoke path a direct <c>tools/call</c> and the gateway's <c>mcp_call_tool</c>/<c>mcp_call_tools</c> share,
/// and the visibility gate <c>tools/list</c>, search and describe share.
/// </summary>
public class ToolInvokerTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IAbpMcpDispatcher _dispatcher = Substitute.For<IAbpMcpDispatcher>();
    private readonly IDisabledToolsProvider _disabledTools = Substitute.For<IDisabledToolsProvider>();
    private readonly IDynamicMcpToolRegistry _registry = Substitute.For<IDynamicMcpToolRegistry>();

    public ToolInvokerTests()
    {
        _disabledTools.GetDisabledToolsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal) { "Off_Tool" });
        _registry.TryGetByName(Arg.Any<string>(), out Arg.Any<ToolDescriptor?>()).Returns(call =>
        {
            var name = call.ArgAt<string>(0);
            call[1] = name is "List_Tool" or "Off_Tool" ? ToolListingResolverTests.Tool(name) with
            {
                OutputSchema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
            } : null;
            return call[1] is not null;
        });
    }

    [Fact]
    public async Task DisabledTool_IsRefused_BeforeTheDispatcher()
    {
        var outcome = await NewInvoker().InvokeAsync("Off_Tool", NoArguments, CancellationToken.None);

        outcome.Should().Be(AbpMcpToolCallOutcome.Error(AbpMcpErrorCodes.Disabled, "Tool 'Off_Tool' is administratively disabled."));
        await _dispatcher.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default, default);
    }

    [Fact]
    public async Task UnknownTool_IsReportedAsUnknown()
    {
        var outcome = await NewInvoker().InvokeAsync("Nope", NoArguments, CancellationToken.None);

        outcome.Text.Should().Be("[UNKNOWN_TOOL] No tool named 'Nope' is registered.");
    }

    [Fact]
    public async Task CodedDispatcherError_BecomesAnErrorOutcome()
    {
        _dispatcher.InvokeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<Task<JsonElement>>(_ => throw new AbpMcpToolException(AbpMcpErrorCodes.Forbidden, "Missing permission: X"));

        var outcome = await NewInvoker().InvokeAsync("List_Tool", NoArguments, CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Be("[FORBIDDEN] Missing permission: X");
        outcome.StructuredContent.Should().BeNull();
    }

    [Fact]
    public async Task Success_CarriesTheTextAndTheWrappedStructuredContent()
    {
        _dispatcher.InvokeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[1,2]").RootElement.Clone());

        var outcome = await NewInvoker().InvokeAsync("List_Tool", NoArguments, CancellationToken.None);

        outcome.IsError.Should().BeFalse();
        outcome.Text.Should().Be("[1,2]");
        outcome.StructuredContent!.Value.GetRawText().Should().Be("""{"result":[1,2]}""");
    }

    [Fact]
    public async Task Visibility_DropsDisabledAndRefusedTools_AndKeepsTheOrder()
    {
        var authorizer = Substitute.For<IAbpMcpToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<HttpContext?>())
            .Returns(call => call.Arg<ToolDescriptor>().Name == "Secret_Tool"
                ? AbpMcpToolAuthorizationResult.Fail("Missing permission: S")
                : AbpMcpToolAuthorizationResult.Success());
        var visibility = new AbpMcpToolVisibility(authorizer, _disabledTools);

        var visible = await visibility.FilterAsync(
            new[] { "Z_Tool", "Off_Tool", "Secret_Tool", "A_Tool" }.Select(ToolListingResolverTests.Tool),
            new DefaultHttpContext(),
            CancellationToken.None);

        visible.Select(t => t.Name).Should().Equal("Z_Tool", "A_Tool");
    }

    private AbpMcpToolInvoker NewInvoker() =>
        new(_registry, _dispatcher, _disabledTools, NullLogger<AbpMcpToolInvoker>.Instance);
}
