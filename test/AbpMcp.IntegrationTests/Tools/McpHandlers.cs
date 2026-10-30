using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Drives the real <c>tools/list</c> and <c>tools/call</c> handlers abp-mcp installs on
/// <see cref="McpServerOptions"/>, including the SDK's own <see cref="Tool"/> validation, without
/// a transport. The session is a substitute; the handlers only use it to track the session.
/// </summary>
internal sealed class McpHandlers
{
    private readonly McpServerHandlers _handlers;

    public McpHandlers(IServiceProvider services) =>
        _handlers = services.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers;

    public async Task<IList<Tool>> ListAsync()
    {
        var context = new RequestContext<ListToolsRequestParams>(
            Substitute.For<McpServer>(), new JsonRpcRequest { Method = RequestMethods.ToolsList }, new ListToolsRequestParams());

        var result = await _handlers.ListToolsHandler!(context, CancellationToken.None);
        return result.Tools;
    }

    public async Task<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments = null)
    {
        var request = new CallToolRequestParams { Name = name, Arguments = arguments };
        var context = new RequestContext<CallToolRequestParams>(
            Substitute.For<McpServer>(), new JsonRpcRequest { Method = RequestMethods.ToolsCall }, request);

        return await _handlers.CallToolHandler!(context, CancellationToken.None);
    }
}
