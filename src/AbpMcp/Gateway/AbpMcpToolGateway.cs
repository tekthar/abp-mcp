using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbpMcp.Gateway;

/// <summary>
/// Default <see cref="IAbpMcpToolGateway"/>. Catches the <see cref="AbpMcpToolException"/> its argument checks raise;
/// the tools it calls report their own errors through <see cref="IAbpMcpToolInvoker"/>. Each call it makes runs in a
/// DI scope of its own, as a separate <c>tools/call</c> request would.
/// </summary>
internal sealed class AbpMcpToolGateway : IAbpMcpToolGateway
{
    private readonly IDynamicMcpToolRegistry _registry;
    private readonly IAbpMcpToolVisibility _visibility;
    private readonly IAbpMcpToolInvoker _invoker;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<AbpMcpToolGateway> _logger;

    public AbpMcpToolGateway(
        IDynamicMcpToolRegistry registry,
        IAbpMcpToolVisibility visibility,
        IAbpMcpToolInvoker invoker,
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<AbpMcpToolGateway>? logger = null)
    {
        _registry = registry;
        _visibility = visibility;
        _invoker = invoker;
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<AbpMcpToolGateway>.Instance;
    }

    private HttpContext? Http => _httpContextAccessor.HttpContext;

    public IReadOnlyList<ToolDescriptor> GetListedTools() =>
        IsEnabled() ? GatewayToolDefinitions.Tools : Array.Empty<ToolDescriptor>();

    public bool Handles(string toolName) => AbpMcpGatewayToolNames.IsGatewayTool(toolName) && IsEnabled();

    public async Task<AbpMcpToolCallOutcome> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var definition = GatewayToolDefinitions.Tools.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        _registry.Initialize();

        // The kill switch applies to gateway tools too, through the same gate tools/list hides them with.
        if ((await _visibility.FilterAsync([definition], Http, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return AbpMcpToolCallOutcome.Error(AbpMcpErrorCodes.Disabled, $"Tool '{toolName}' is administratively disabled.");
        }

        try
        {
            return toolName switch
            {
                AbpMcpGatewayToolNames.Search => await SearchAsync(arguments, cancellationToken).ConfigureAwait(false),
                AbpMcpGatewayToolNames.Describe => await DescribeAsync(arguments, cancellationToken).ConfigureAwait(false),
                AbpMcpGatewayToolNames.Call => await CallOneAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => await CallManyAsync(arguments, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (AbpMcpToolException invalid)
        {
            return AbpMcpToolCallOutcome.Error(invalid.Code, invalid.Message);
        }
    }

    private bool IsEnabled()
    {
        _registry.Initialize();
        return GatewayToolDefinitions.IsEnabled(_registry.Tools);
    }

    /// <summary>Rank every registered tool, keep those the caller may see (in rank order), return the first <c>limit</c>.</summary>
    private async Task<AbpMcpToolCallOutcome> SearchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = GatewayArguments.SearchQuery(arguments);
        var limit = GatewayArguments.Limit(arguments);

        var ranked = ToolSearchIndex.For(_registry.Tools).Search(query, cancellationToken);
        var visible = await _visibility.FilterAsync(ranked.Select(m => m.Tool), Http, cancellationToken).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["tools"] = new JsonArray(visible.Take(limit).Select(t => (JsonNode)GatewayResults.SearchItem(t)).ToArray()),
            ["total_matches"] = visible.Count,
        };

        if (visible.Count == 0)
        {
            payload["hint"] = "No tool matched. Try fewer or more general keywords, or part of a tool name.";
        }

        return GatewayResults.Success(payload);
    }

    /// <summary>
    /// Describe the requested tools the caller may see. Unknown names and names the caller may not see are both reported
    /// in <c>not_found</c>, so the answer never tells a caller that a tool it cannot use exists.
    /// </summary>
    private async Task<AbpMcpToolCallOutcome> DescribeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var names = GatewayArguments.Names(arguments);

        var known = names
            .Select(name => _registry.TryGetByName(name, out var tool) ? tool : null)
            .OfType<ToolDescriptor>();
        var visible = await _visibility.FilterAsync(known, Http, cancellationToken).ConfigureAwait(false);
        var found = visible.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        return GatewayResults.Success(new JsonObject
        {
            ["tools"] = new JsonArray(visible.Select(t => (JsonNode)GatewayResults.ProtocolTool(t)).ToArray()),
            ["not_found"] = new JsonArray(names.Where(n => !found.Contains(n)).Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
        });
    }

    /// <summary>The tool's own result, unchanged: what a direct <c>tools/call</c> would return.</summary>
    private Task<AbpMcpToolCallOutcome> CallOneAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var call = GatewayArguments.Call(arguments, path: string.Empty, via: AbpMcpGatewayToolNames.Call);
        return InvokeInOwnScopeAsync(call, cancellationToken);
    }

    /// <summary>
    /// Every call goes through the invoker on its own, in a DI scope of its own, so each gets its own permission check,
    /// unit of work, audit entry and scoped services: state one call leaves behind (an injected DbContext's change
    /// tracker) cannot be saved by the next. Both modes run the calls one at a time, in order. <c>parallel</c> means the
    /// calls are independent: each runs whatever the others return. A cancelled request starts no further call, and a
    /// call that fails outside the error mapping is reported as <c>INTERNAL</c> rather than discarding the results of
    /// the calls that already ran.
    /// </summary>
    private async Task<AbpMcpToolCallOutcome> CallManyAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var calls = GatewayArguments.Calls(arguments);
        var sequential = GatewayArguments.IsSequential(arguments);

        var results = new JsonArray();
        int succeeded = 0, failed = 0, skipped = 0;
        foreach (var call in calls)
        {
            // A direct tools/call starts nothing once its request is cancelled; a batch must not either.
            cancellationToken.ThrowIfCancellationRequested();

            if (sequential && failed > 0)
            {
                skipped++;
                results.Add(GatewayResults.BatchItem(call.Name, outcome: null));
                continue;
            }

            var outcome = await InvokeReportingFailureAsync(call, cancellationToken).ConfigureAwait(false);
            if (outcome.IsError)
            {
                failed++;
            }
            else
            {
                succeeded++;
            }

            results.Add(GatewayResults.BatchItem(call.Name, outcome));
        }

        return GatewayResults.Success(new JsonObject
        {
            ["mode"] = sequential ? "sequential" : "parallel",
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["results"] = results,
        });
    }

    /// <summary>
    /// One batch call. An exception the invoker lets through (a failed kill-switch read, an authorization handler that
    /// throws) becomes an <c>INTERNAL</c> item that says the call may have run, so the calls before it keep their
    /// results; cancellation still ends the request.
    /// </summary>
    private async Task<AbpMcpToolCallOutcome> InvokeReportingFailureAsync(GatewayArguments.ToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            return await InvokeInOwnScopeAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Batch call {Tool} failed outside the error mapping table", call.Name);
            return AbpMcpToolCallOutcome.Error(AbpMcpErrorCodes.Internal, $"Tool '{call.Name}' failed with an internal error; it may have run.");
        }
    }

    /// <summary>
    /// One call in a fresh DI scope, as a separate <c>tools/call</c> request would have, with
    /// <see cref="HttpContext.RequestServices"/> pointing at it for the duration of the call. Without a request scope
    /// (tests that drive the gateway directly) the gateway's own invoker is used.
    /// </summary>
    private async Task<AbpMcpToolCallOutcome> InvokeInOwnScopeAsync(GatewayArguments.ToolCall call, CancellationToken cancellationToken)
    {
        var http = Http;
        if (_scopeFactory is null || http?.RequestServices is null)
        {
            return await _invoker.InvokeAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
        }

        var requestServices = http.RequestServices;
        await using var scope = _scopeFactory.CreateAsyncScope();
        http.RequestServices = scope.ServiceProvider;
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IAbpMcpToolInvoker>()
                .InvokeAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            http.RequestServices = requestServices;
        }
    }
}
