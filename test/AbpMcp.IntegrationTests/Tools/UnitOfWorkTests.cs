using System.Text.Json;
using AbpMcp.Dispatch;
using AbpMcp.IntegrationTests.Ledger;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Regression: the dispatcher invoked the concrete (sealed, non-virtual) service by reflection and
/// opened no unit of work, and the MCP endpoint is outside the MVC pipeline whose UoW filter makes
/// a REST call transactional. Each repository call then ran in its own interceptor-created unit of
/// work, so a tool that wrote and then threw left its earlier writes committed — a partial state
/// the parallel HTTP endpoint would have rolled back.
/// </summary>
public sealed class UnitOfWorkTests : AbpMcpIntegrationTestBase
{
    [Fact]
    public async Task FailingTool_RollsBackItsEarlierWrites()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var act = async () => await InvokeAsync(services, "Ledger_RecordThenFail", """{"note":"partial"}""");

            var thrown = await act.Should().ThrowAsync<AbpMcpToolException>();
            thrown.Which.Code.Should().Be(LedgerAppService.FailureCode);

            (await CountEntriesAsync(services)).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task SuccessfulTool_CommitsItsWrites_InATransaction()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            var observed = await InvokeAsync(services, "Ledger_Record", """{"note":"kept"}""");

            observed.GetProperty("hasUnitOfWork").GetBoolean().Should().BeTrue();
            observed.GetProperty("isTransactional").GetBoolean().Should().BeTrue();
            (await CountEntriesAsync(services)).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task ReadTool_RunsInANonTransactionalUnitOfWork()
    {
        // ABP's REST UoW filter opens no transaction for a GET action. Every tools/call is a POST, so the
        // dispatcher must decide by the parallel action's verb, or every Get/GetList tool pays for one.
        await RunAsAuthenticatedAsync(async services =>
        {
            var observed = await InvokeAsync(services, "Ledger_GetSnapshot", "{}");

            observed.GetProperty("hasUnitOfWork").GetBoolean().Should().BeTrue();
            observed.GetProperty("isTransactional").GetBoolean().Should().BeFalse();
            return true;
        });
    }

    private static async Task<JsonElement> InvokeAsync(IServiceProvider services, string toolName, string json)
    {
        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();
        registry.TryGetByName(toolName, out var descriptor).Should().BeTrue();

        using var args = JsonDocument.Parse(json);
        return await services.GetRequiredService<IAbpMcpDispatcher>()
            .InvokeAsync(descriptor!, args.RootElement, CancellationToken.None);
    }

    private static async Task<long> CountEntriesAsync(IServiceProvider services)
    {
        using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        var count = await services.GetRequiredService<IRepository<LedgerEntry, Guid>>().GetCountAsync();
        await uow.CompleteAsync();
        return count;
    }
}
