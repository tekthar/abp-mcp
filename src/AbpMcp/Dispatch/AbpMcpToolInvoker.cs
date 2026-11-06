using System.Text.Json;
using AbpMcp.Registration;
using AbpMcp.Settings;
using Microsoft.Extensions.Logging;

namespace AbpMcp.Dispatch;

/// <summary>
/// Default <see cref="IAbpMcpToolInvoker"/>. Catches only <see cref="AbpMcpToolException"/>, which the dispatcher
/// raises after <see cref="AbpMcpExceptionMapper"/> has translated whatever the tool threw; nothing else is swallowed.
/// </summary>
internal sealed class AbpMcpToolInvoker : IAbpMcpToolInvoker
{
    private readonly IDynamicMcpToolRegistry _registry;
    private readonly IAbpMcpDispatcher _dispatcher;
    private readonly IDisabledToolsProvider _disabledTools;
    private readonly ILogger<AbpMcpToolInvoker> _logger;

    public AbpMcpToolInvoker(
        IDynamicMcpToolRegistry registry,
        IAbpMcpDispatcher dispatcher,
        IDisabledToolsProvider disabledTools,
        ILogger<AbpMcpToolInvoker> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _disabledTools = disabledTools;
        _logger = logger;
    }

    public async Task<AbpMcpToolCallOutcome> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        var disabledTools = await _disabledTools.GetDisabledToolsAsync(cancellationToken).ConfigureAwait(false);
        if (disabledTools.Contains(toolName))
        {
            return AbpMcpToolCallOutcome.Error(AbpMcpErrorCodes.Disabled, $"Tool '{toolName}' is administratively disabled.");
        }

        if (!_registry.TryGetByName(toolName, out var descriptor) || descriptor is null)
        {
            return AbpMcpToolCallOutcome.Error(AbpMcpErrorCodes.UnknownTool, $"No tool named '{toolName}' is registered.");
        }

        try
        {
            var payload = await _dispatcher.InvokeAsync(descriptor, arguments, cancellationToken).ConfigureAwait(false);
            return AbpMcpToolCallOutcome.Success(
                payload.GetRawText(),
                McpOutputShape.ToStructuredContent(descriptor.OutputSchema, payload));
        }
        catch (AbpMcpToolException tex)
        {
            _logger.LogInformation(tex, "Tool {Tool} returned error {Code}", toolName, tex.Code);
            return AbpMcpToolCallOutcome.Error(tex.Code, tex.Message);
        }
    }
}
