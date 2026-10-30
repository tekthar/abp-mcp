using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.IntegrationTests.Premium;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// A tool call gets the feature checks and audit logging ABP's MVC filters give the parallel REST
/// action, whether or not the app-service method is virtual (non-virtual methods are invisible to ABP's
/// class-proxy interceptors).
/// </summary>
public sealed class CrossCuttingConcernTests : AbpMcpIntegrationTestBase
{
    [Theory]
    [InlineData(nameof(PremiumAppService.ExportAsync))]
    [InlineData(nameof(PremiumAppService.VirtualExportAsync))]
    public async Task RequiresFeature_Disabled_IsRefused(string method)
    {
        var act = () => InvokeAsync<PremiumAppService>(method);

        var error = (await act.Should().ThrowAsync<AbpMcpToolException>()).Which;
        error.Code.Should().Be("FORBIDDEN");
        error.Message.Should().Contain(PremiumFeatures.Disabled);
    }

    [Fact]
    public async Task RequiresFeature_Enabled_Runs()
    {
        var result = await InvokeAsync<PremiumAppService>(nameof(PremiumAppService.ReportAsync));

        result.GetString().Should().Be("reported");
    }

    [Fact]
    public async Task RequiresGlobalFeature_Disabled_IsRefused()
    {
        var act = () => InvokeAsync<ArchivedAppService>(nameof(ArchivedAppService.RestoreAsync));

        (await act.Should().ThrowAsync<AbpMcpToolException>()).Which.Code.Should().Be("DISABLED");
    }

    [Theory]
    [InlineData(nameof(PremiumAppService.PingAsync))]
    [InlineData(nameof(PremiumAppService.VirtualPingAsync))]
    public async Task ServiceCall_IsAuditedExactlyOnce(string method)
    {
        await InvokeAsync<PremiumAppService>(method);

        var actions = AuditedActions(method);
        actions.Should().HaveCount(1, "the call is audited, and a virtual method is not audited twice");
        actions[0].ServiceName.Should().Be(typeof(PremiumAppService).FullName);
    }

    [Fact]
    public async Task FailedServiceCall_IsAuditedWithItsException()
    {
        var act = () => InvokeAsync<PremiumAppService>(nameof(PremiumAppService.FailAsync));
        await act.Should().ThrowAsync<AbpMcpToolException>();

        var log = Store.Logs.Single(l => l.Actions.Any(a => a.MethodName == nameof(PremiumAppService.FailAsync)));
        log.Exceptions.Should().ContainSingle().Which.Message.Should().Be("boom");
    }

    [Fact]
    public async Task DisableAuditing_IsHonored()
    {
        await InvokeAsync<PremiumAppService>(nameof(PremiumAppService.UnauditedAsync));

        AuditedActions(nameof(PremiumAppService.UnauditedAsync)).Should().BeEmpty();
    }

    private RecordingAuditingStore Store => (RecordingAuditingStore)ServiceProvider.GetRequiredService<Volo.Abp.Auditing.IAuditingStore>();

    private Volo.Abp.Auditing.AuditLogActionInfo[] AuditedActions(string method) =>
        Store.Logs.SelectMany(l => l.Actions).Where(a => a.MethodName == method).ToArray();

    private Task<JsonElement> InvokeAsync<TService>(string method) =>
        RunAsAuthenticatedAsync(services =>
        {
            var descriptor = new ToolDescriptor
            {
                Name = $"{typeof(TService).Name}_{method}",
                Description = "Cross-cutting concern fixture.",
                InputSchema = new JsonObject { ["type"] = "object" },
                ParameterNames = [],
                RequiredPermissions = [],
                ServiceType = typeof(TService),
                Method = typeof(TService).GetMethod(method),
                HttpMethod = "POST",
            };

            return services.GetRequiredService<IAbpMcpDispatcher>()
                .InvokeAsync(descriptor, JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        });
}
