using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using AbpMcp.Dispatch;
using AbpMcp.Gateway;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AbpMcp.Registration;

/// <summary>
/// Bridges <see cref="IDynamicMcpToolRegistry"/> descriptors, the gateway tools of <see cref="IAbpMcpToolGateway"/>
/// and <see cref="IAbpMcpToolInvoker"/> invocations to Microsoft's <see cref="McpServerHandlers"/> via the
/// <c>ListTools</c> and <c>CallTool</c> handler properties on <see cref="McpServerOptions"/>.
/// </summary>
/// <remarks>
/// This is the single seam between abp-mcp and the MCP SDK. If the SDK's handler shape ever
/// changes, this is the only file that needs to move. <c>tools/list</c> and <c>tools/call</c>
/// are the entire wire surface for MCP tools; nothing else here pretends otherwise.
/// </remarks>
internal sealed class AbpMcpHandlerWiring : IConfigureOptions<McpServerOptions>
{
    private readonly IServiceProvider _rootServices;

    public AbpMcpHandlerWiring(IServiceProvider rootServices)
    {
        _rootServices = rootServices;
    }

    public void Configure(McpServerOptions options)
    {
        options.Handlers.ListToolsHandler = HandleListAsync;
        options.Handlers.CallToolHandler = HandleCallAsync;
    }

    /// <summary>
    /// The listed tools the caller may see, then the gateway tools while any tool is on demand. Everything goes through
    /// <see cref="IAbpMcpToolVisibility"/>, the gate the dispatcher's own checks mirror, so list and call cannot disagree.
    /// </summary>
    private async ValueTask<ListToolsResult> HandleListAsync(
        RequestContext<ListToolsRequestParams> context,
        CancellationToken cancellationToken)
    {
        var (services, http) = ResolveRequestServices();
        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        var gateway = services.GetRequiredService<IAbpMcpToolGateway>();

        registry.Initialize();

        var candidates = registry.Tools
            .Where(descriptor => descriptor.Listing == McpToolListing.Listed)
            .Concat(gateway.GetListedTools());
        var visible = await services.GetRequiredService<IAbpMcpToolVisibility>()
            .FilterAsync(candidates, http, cancellationToken).ConfigureAwait(false);

        return new ListToolsResult { Tools = visible.Select(ToProtocolTool).ToList() };
    }

    /// <summary>
    /// A gateway tool runs in <see cref="IAbpMcpToolGateway"/>; any other name, listed or on demand, goes through
    /// <see cref="IAbpMcpToolInvoker"/>, the same path the gateway's <c>mcp_call_tool</c> takes.
    /// </summary>
    private async ValueTask<CallToolResult> HandleCallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var requestParams = context.Params
            ?? throw new InvalidOperationException("CallTool request received without params.");
        var toolName = requestParams.Name;
        var arguments = NormalizeArguments(requestParams.Arguments);

        var (services, _) = ResolveRequestServices();
        var gateway = services.GetRequiredService<IAbpMcpToolGateway>();

        var outcome = gateway.Handles(toolName)
            ? await gateway.CallAsync(toolName, arguments, cancellationToken).ConfigureAwait(false)
            : await services.GetRequiredService<IAbpMcpToolInvoker>()
                .InvokeAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);

        return ToCallToolResult(outcome);
    }

    /// <summary>
    /// Resolve a per-request scope. We prefer the active <see cref="HttpContext.RequestServices"/>
    /// when one exists (the MCP transport is always over HTTP for v0.1) so DI scoping matches the
    /// surrounding request. Falling back to the root provider only happens in unit tests that drive
    /// the handlers directly.
    /// </summary>
    private (IServiceProvider services, HttpContext? http) ResolveRequestServices()
    {
        var httpAccessor = _rootServices.GetRequiredService<IHttpContextAccessor>();
        var http = httpAccessor.HttpContext;
        if (http is not null)
        {
            return (http.RequestServices, http);
        }

        return (_rootServices, http: null);
    }

    private static Tool ToProtocolTool(ToolDescriptor descriptor)
    {
        var tool = new Tool
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            InputSchema = JsonSerializer.SerializeToElement(descriptor.InputSchema),
        };

        var outputSchema = McpOutputShape.ToProtocolSchema(descriptor.OutputSchema);
        if (outputSchema is not null)
        {
            tool.OutputSchema = JsonSerializer.SerializeToElement(outputSchema);
        }

        return tool;
    }

    /// <summary>
    /// MCP transports surface tool arguments as a JSON object. The dispatcher accepts a single
    /// <see cref="JsonElement"/>; this normalizer accepts either an already-shaped element or a
    /// dictionary form (used by some SDK versions) and turns it into the object shape the dispatcher expects.
    /// </summary>
    private static JsonElement NormalizeArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        var node = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            node[key] = JsonNode.Parse(value.GetRawText());
        }

        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The protocol result: one text block, plus <c>structuredContent</c> when the outcome has it.</summary>
    private static CallToolResult ToCallToolResult(AbpMcpToolCallOutcome outcome) => new()
    {
        IsError = outcome.IsError,
        StructuredContent = outcome.StructuredContent,
        Content = new[]
        {
            new TextContentBlock { Text = outcome.Text } as ContentBlock,
        },
    };
}
