using AbpMcp.IntegrationTests.Books;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

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
            var mcp = new McpHandlers(services);
            var monitor = services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>();

            (await ListNamesAsync(mcp)).Should().Contain(Tool);

            monitor.CurrentValue.DisabledTools.Add(Tool);
            try
            {
                (await ListNamesAsync(mcp)).Should().NotContain(Tool);
            }
            finally
            {
                monitor.CurrentValue.DisabledTools.Remove(Tool);
            }

            (await ListNamesAsync(mcp)).Should().Contain(Tool);
            return true;
        });
    }

    [Fact]
    public async Task ToolsCall_RefusesToolDisabledAtRuntime()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var mcp = new McpHandlers(services);
            var monitor = services.GetRequiredService<IOptionsMonitor<AbpMcpOptions>>();

            monitor.CurrentValue.DisabledTools.Add(Tool);
            try
            {
                var result = await mcp.CallAsync(Tool);

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

    private static async Task<IReadOnlyList<string>> ListNamesAsync(McpHandlers mcp) =>
        (await mcp.ListAsync()).Select(t => t.Name).ToList();
}
