namespace AbpMcp.Settings;

/// <summary>
/// The single source for which tools are administratively disabled. <c>tools/list</c>,
/// <c>tools/call</c>, the dispatcher and <c>/mcp/_discover</c> all read it, so they cannot disagree.
/// </summary>
/// <remarks>
/// The default implementation combines <see cref="AbpMcpOptions.DisabledTools"/> (in-process, applied
/// immediately) with the <see cref="AbpMcpSettingNames.DisabledTools"/> ABP setting (cross-instance,
/// cached for <see cref="AbpMcpOptions.DisabledToolsSettingCacheDuration"/>). Replace it via DI to source
/// the kill switch from somewhere else.
/// </remarks>
public interface IDisabledToolsProvider
{
    /// <summary>The names of the currently disabled tools (ordinal comparison).</summary>
    Task<IReadOnlySet<string>> GetDisabledToolsAsync(CancellationToken cancellationToken = default);
}
