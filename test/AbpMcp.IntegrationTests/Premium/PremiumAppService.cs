using Volo.Abp.Application.Services;
using Volo.Abp.Auditing;
using Volo.Abp.Features;
using Volo.Abp.GlobalFeatures;
using Volo.Abp.Validation.StringValues;

namespace AbpMcp.IntegrationTests.Premium;

/// <summary>
/// A real <see cref="ApplicationService"/> (so ABP's interceptors are registered for it) with the
/// non-virtual methods ABP's templates generate next to virtual ones, to prove the MCP path enforces
/// feature checks and audits both shapes exactly once.
/// </summary>
public class PremiumAppService : ApplicationService
{
    public Task<string> PingAsync() => Task.FromResult("pong");

    public virtual Task<string> VirtualPingAsync() => Task.FromResult("pong");

    [RequiresFeature(PremiumFeatures.Disabled)]
    public Task<string> ExportAsync() => Task.FromResult("exported");

    [RequiresFeature(PremiumFeatures.Disabled)]
    public virtual Task<string> VirtualExportAsync() => Task.FromResult("exported");

    [RequiresFeature(PremiumFeatures.Enabled)]
    public Task<string> ReportAsync() => Task.FromResult("reported");

    [DisableAuditing]
    public Task<string> UnauditedAsync() => Task.FromResult("quiet");

    public Task<string> FailAsync() => throw new InvalidOperationException("boom");
}

/// <summary>A service whose whole surface belongs to a global feature that is never enabled.</summary>
[RequiresGlobalFeature("AbpMcp.Tests.NeverEnabled")]
public class ArchivedAppService : ApplicationService
{
    public Task<string> RestoreAsync() => Task.FromResult("restored");
}

public static class PremiumFeatures
{
    public const string Disabled = "Premium.Disabled";
    public const string Enabled = "Premium.Enabled";
}

public sealed class PremiumFeatureDefinitionProvider : FeatureDefinitionProvider
{
    public override void Define(IFeatureDefinitionContext context)
    {
        var group = context.AddGroup("Premium");
        group.AddFeature(PremiumFeatures.Disabled, defaultValue: "false", valueType: new ToggleStringValueType());
        group.AddFeature(PremiumFeatures.Enabled, defaultValue: "true", valueType: new ToggleStringValueType());
    }
}

/// <summary>Captures saved audit logs in memory so tests can assert on them.</summary>
public sealed class RecordingAuditingStore : IAuditingStore
{
    private readonly List<AuditLogInfo> _logs = new();

    public IReadOnlyList<AuditLogInfo> Logs
    {
        get { lock (_logs) { return _logs.ToArray(); } }
    }

    public Task SaveAsync(AuditLogInfo auditInfo)
    {
        lock (_logs)
        {
            _logs.Add(auditInfo);
        }

        return Task.CompletedTask;
    }
}
