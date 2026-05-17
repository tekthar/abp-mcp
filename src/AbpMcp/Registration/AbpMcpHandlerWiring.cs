using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AbpMcp.Registration;

/// <summary>
/// Bridges <see cref="IDynamicMcpToolRegistry"/> descriptors and <see cref="IAbpMcpDispatcher"/>
/// invocations to Microsoft's <see cref="McpServerHandlers"/> via the <c>ListTools</c> and
/// <c>CallTool</c> handler properties on <see cref="McpServerOptions"/>.
/// </summary>
/// <remarks>
/// This is the single seam between abp-mcp and the MCP SDK. If the SDK's handler shape ever
/// changes, this is the only file that needs to move. <c>tools/list</c> and <c>tools/call</c>
/// are the entire wire surface for MCP tools; nothing else here pretends otherwise.
/// </remarks>
internal sealed class AbpMcpHandlerWiring : IConfigureOptions<McpServerOptions>
{
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<AbpMcpHandlerWiring> _logger;

    public AbpMcpHandlerWiring(
        IServiceProvider rootServices,
        ILogger<AbpMcpHandlerWiring> logger)
    {
        _rootServices = rootServices;
        _logger = logger;
    }

    public void Configure(McpServerOptions options)
    {
        options.Handlers.ListToolsHandler = HandleListAsync;
        options.Handlers.CallToolHandler = HandleCallAsync;
    }

    private async ValueTask<ListToolsResult> HandleListAsync(
        RequestContext<ListToolsRequestParams> context,
        CancellationToken cancellationToken)
    {
        var (services, user) = ResolveRequestServices();
        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var abpOptions = services.GetRequiredService<IOptions<AbpMcpOptions>>().Value;

        registry.Initialize();

        var visibleTools = new List<Tool>(registry.Tools.Count);
        foreach (var descriptor in registry.Tools)
        {
            if (abpOptions.DisabledTools.Contains(descriptor.Name))
            {
                continue;
            }

            if (!await IsAuthorizedAsync(authorization, user, descriptor).ConfigureAwait(false))
            {
                continue;
            }

            visibleTools.Add(ToProtocolTool(descriptor));
        }

        return new ListToolsResult { Tools = visibleTools };
    }

    private async ValueTask<CallToolResult> HandleCallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var requestParams = context.Params
            ?? throw new InvalidOperationException("CallTool request received without params.");
        var toolName = requestParams.Name;

        var (services, _) = ResolveRequestServices();
        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
        var abpOptions = services.GetRequiredService<IOptions<AbpMcpOptions>>().Value;

        if (abpOptions.DisabledTools.Contains(toolName))
        {
            return ErrorResult(AbpMcpErrorCodes.Disabled, $"Tool '{toolName}' is administratively disabled.");
        }

        if (!registry.TryGetByName(toolName, out var descriptor) || descriptor is null)
        {
            return ErrorResult(AbpMcpErrorCodes.UnknownTool, $"No tool named '{toolName}' is registered.");
        }

        try
        {
            var arguments = NormalizeArguments(requestParams.Arguments);
            var result = await dispatcher.InvokeAsync(descriptor, arguments, cancellationToken)
                .ConfigureAwait(false);

            return SuccessResult(result);
        }
        catch (AbpMcpToolException tex)
        {
            _logger.LogInformation(tex, "Tool {Tool} returned error {Code}", toolName, tex.Code);
            return ErrorResult(tex.Code, tex.Message);
        }
    }

    /// <summary>
    /// Resolve a per-request scope. We prefer the active <see cref="HttpContext.RequestServices"/>
    /// when one exists (the MCP transport is always over HTTP for v0.1) so DI scoping matches the
    /// surrounding request. Falling back to the root provider only happens in unit tests that drive
    /// the handlers directly.
    /// </summary>
    private (IServiceProvider services, System.Security.Claims.ClaimsPrincipal? user) ResolveRequestServices()
    {
        var httpAccessor = _rootServices.GetRequiredService<IHttpContextAccessor>();
        var http = httpAccessor.HttpContext;
        if (http is not null)
        {
            return (http.RequestServices, http.User);
        }

        return (_rootServices, user: null);
    }

    private static async Task<bool> IsAuthorizedAsync(
        IAuthorizationService authorization,
        System.Security.Claims.ClaimsPrincipal? user,
        ToolDescriptor descriptor)
    {
        if (descriptor.RequiredPermissions.Count == 0)
        {
            return true;
        }

        if (user is null)
        {
            return false;
        }

        foreach (var permission in descriptor.RequiredPermissions)
        {
            var result = await authorization.AuthorizeAsync(user, permission).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    private static Tool ToProtocolTool(ToolDescriptor descriptor) => new()
    {
        Name = descriptor.Name,
        Description = descriptor.Description,
        InputSchema = JsonSerializer.SerializeToElement(descriptor.InputSchema),
    };

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

    private static CallToolResult SuccessResult(JsonElement payload) => new()
    {
        IsError = false,
        StructuredContent = payload,
        Content = new[]
        {
            new TextContentBlock { Text = payload.GetRawText() } as ContentBlock,
        },
    };

    private static CallToolResult ErrorResult(string code, string message) => new()
    {
        IsError = true,
        Content = new[]
        {
            new TextContentBlock { Text = $"[{code}] {message}" } as ContentBlock,
        },
    };
}
