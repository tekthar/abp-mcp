using AbpMcp.IntegrationTests.Books;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Regression for #13 Stage A: <c>DisabledTools</c> changes made through
/// <see cref="IOptionsMonitor{TOptions}"/> must be honored by the MCP handlers themselves, not just
/// the dispatcher. The handlers previously read <see cref="IOptions{TOptions}"/>, a separate cached
/// instance, so a hot-disabled tool kept appearing in <c>tools/list</c>.
/// </summary>
public sealed class DisabledToolsHotReloadTests : AbpMcpIntegrationTestBase
{
    private const string Tool = "Book_Create";

    [Fact]
    public async Task ToolsList_DropsToolDisabledAtRuntime()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var handlers = services.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers;
            var monitor = services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>();

            (await ListToolNamesAsync(handlers)).Should().Contain(Tool);

            monitor.CurrentValue.DisabledTools.Add(Tool);
            try
            {
                (await ListToolNamesAsync(handlers)).Should().NotContain(Tool);
            }
            finally
            {
                monitor.CurrentValue.DisabledTools.Remove(Tool);
            }

            (await ListToolNamesAsync(handlers)).Should().Contain(Tool);
            return true;
        });
    }

    [Fact]
    public async Task ToolsCall_RefusesToolDisabledAtRuntime()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var handlers = services.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers;
            var monitor = services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>();

            monitor.CurrentValue.DisabledTools.Add(Tool);
            try
            {
                var request = new CallToolRequestParams { Name = Tool };
                var context = new RequestContext<CallToolRequestParams>(
                    Substitute.For<McpServer>(), new JsonRpcRequest { Method = RequestMethods.ToolsCall }, request);

                var result = await handlers.CallToolHandler!(context, CancellationToken.None);

                result.IsError.Should().BeTrue();
                result.Content.OfType<TextContentBlock>().Single().Text.Should().StartWith("[DISABLED]");

                var db = services.GetRequiredService<BookDbContext>();
                (await db.Books.CountAsync()).Should().Be(0);
            }
            finally
            {
                monitor.CurrentValue.DisabledTools.Remove(Tool);
            }

            return true;
        });
    }

    private static async Task<IReadOnlyList<string>> ListToolNamesAsync(McpServerHandlers handlers)
    {
        var context = new RequestContext<ListToolsRequestParams>(
            Substitute.For<McpServer>(), new JsonRpcRequest { Method = RequestMethods.ToolsList }, new ListToolsRequestParams());

        var result = await handlers.ListToolsHandler!(context, CancellationToken.None);
        return result.Tools.Select(t => t.Name).ToList();
    }
}
