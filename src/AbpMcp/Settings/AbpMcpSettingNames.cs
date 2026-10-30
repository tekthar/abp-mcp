namespace AbpMcp.Settings;

/// <summary>Names of the ABP settings abp-mcp defines.</summary>
public static class AbpMcpSettingNames
{
    /// <summary>
    /// Cross-instance kill switch: a comma-separated list of tool names to disable, combined with
    /// <see cref="AbpMcpOptions.DisabledTools"/>. Global only (no tenant or user overrides). Set it in
    /// <c>appsettings.json</c> under <c>Settings:AbpMcp.DisabledTools</c>, or, with ABP Setting
    /// Management installed, as a global setting value. That value is shared by every instance when
    /// they share a distributed cache (e.g. Redis), as multi-instance ABP deployments do.
    /// </summary>
    public const string DisabledTools = "AbpMcp.DisabledTools";
}
