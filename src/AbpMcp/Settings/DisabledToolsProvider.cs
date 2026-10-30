using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Settings;

namespace AbpMcp.Settings;

/// <summary>
/// Default <see cref="IDisabledToolsProvider"/>: <see cref="AbpMcpOptions.DisabledTools"/> united with
/// the <see cref="AbpMcpSettingNames.DisabledTools"/> setting.
/// </summary>
/// <remarks>
/// The options set is read on every call, so in-process changes apply immediately. The setting is
/// cached per instance for <see cref="AbpMcpOptions.DisabledToolsSettingCacheDuration"/>, so
/// <c>tools/call</c> does not pay a settings-store read each time; a change made on another instance
/// therefore applies here within that window, on top of ABP's own setting-cache invalidation. If the
/// setting cannot be read, the last value read is kept and a warning is logged, so a settings-store
/// outage does not take <c>tools/list</c> down with it.
/// </remarks>
internal sealed class DisabledToolsProvider : IDisabledToolsProvider
{
    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AbpMcpOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DisabledToolsProvider> _logger;

    private volatile SettingSnapshot? _snapshot;

    public DisabledToolsProvider(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AbpMcpOptions> options,
        TimeProvider time,
        ILogger<DisabledToolsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>> GetDisabledToolsAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var fromSetting = await GetSettingValueAsync(options.DisabledToolsSettingCacheDuration).ConfigureAwait(false);

        if (fromSetting.Count == 0)
        {
            return new HashSet<string>(options.DisabledTools, StringComparer.Ordinal);
        }

        var disabled = new HashSet<string>(fromSetting, StringComparer.Ordinal);
        disabled.UnionWith(options.DisabledTools);
        return disabled;
    }

    /// <summary>Split a setting value into tool names: comma, semicolon or newline separated, trimmed.</summary>
    internal static IReadOnlySet<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);

    private async Task<IReadOnlySet<string>> GetSettingValueAsync(TimeSpan cacheDuration)
    {
        var now = _time.GetUtcNow();
        var snapshot = _snapshot;
        if (snapshot is not null && now < snapshot.ExpiresAt)
        {
            return snapshot.Tools;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingProvider>();
            var value = await settings.GetOrNullAsync(AbpMcpSettingNames.DisabledTools).ConfigureAwait(false);

            var tools = Parse(value);
            _snapshot = new SettingSnapshot(tools, now + cacheDuration);
            return tools;
        }
        catch (Exception ex)
        {
            // Not the dispatcher: this keeps a store outage from failing every tools/list. Retry on the
            // next call rather than caching the failure.
            _logger.LogWarning(ex, "Could not read the {Setting} setting; using the last known value.", AbpMcpSettingNames.DisabledTools);
            return snapshot?.Tools ?? new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private sealed record SettingSnapshot(IReadOnlySet<string> Tools, DateTimeOffset ExpiresAt);
}
