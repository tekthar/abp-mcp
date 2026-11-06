using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.IntegrationTests.Ledger;
using AbpMcp.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Authorization;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// Exceptions raised inside a tool reach the agent through the mapping table, never as INTERNAL when
/// ABP gives them a specific meaning (#51).
/// </summary>
public sealed class ErrorMappingTests : AbpMcpIntegrationTestBase
{
    private sealed class Widget;

    [Fact]
    public async Task EntityNotFound_MapsToNotFound_WithEntityNameAndId()
    {
        var id = Guid.NewGuid();
        var error = await InvokeThrowingAsync(new EntityNotFoundException(typeof(Widget), id));

        error.Code.Should().Be("NOT_FOUND");
        error.Message.Should().Be($"There is no Widget with id = {id}.");
    }

    [Fact]
    public async Task CodedAuthorizationFailure_NamesTheCode_InsteadOfTheClrDefaultMessage()
    {
        var error = await InvokeThrowingAsync(
            new AbpAuthorizationException(code: "Volo.Feature:010001").WithData("FeatureNames", "Library.Premium"));

        error.Code.Should().Be("FORBIDDEN");
        error.Message.Should().Be("Not authorized (Volo.Feature:010001): FeatureNames=Library.Premium.");
    }

    [Fact]
    public async Task ConcurrencyFailure_MapsToConflict_WithoutLeakingTheDatabaseMessage()
    {
        var error = await InvokeThrowingAsync(
            new AbpDbConcurrencyException("Expected to affect 1 row(s) but actually affected 0 row(s) in AppWidgets."));

        error.Code.Should().Be("CONFLICT");
        error.Message.Should().Be(
            "The record was changed by someone else since it was read. Read it again and retry with the new values.");
    }

    [Fact]
    public async Task LostConcurrencyRace_OnSave_MapsToConflict()
    {
        // The real path: the tool reads a record, another writer saves it meanwhile, and the tool's save
        // fails the ConcurrencyStamp check when the dispatcher completes its unit of work. ABP's EF Core
        // layer raises AbpDbConcurrencyException there, so EF's own exception never reaches the dispatcher.
        var accountId = Guid.NewGuid();
        await WithUnitOfWorkAsync(repo => repo.InsertAsync(new LedgerAccount(accountId, "original")));

        var error = await RunAsAuthenticatedAsync(async services =>
        {
            var descriptor = new ToolDescriptor
            {
                Name = "LedgerAccount_Rename",
                Description = "Renames an account after another writer saved it.",
                InputSchema = new JsonObject { ["type"] = "object" },
                ParameterNames = [],
                RequiredPermissions = [],
                Handler = async (invocation, ct) =>
                {
                    var accounts = invocation.Services.GetRequiredService<IRepository<LedgerAccount, Guid>>();
                    var account = await accounts.GetAsync(accountId, cancellationToken: ct);

                    await WithUnitOfWorkAsync(async repo =>
                    {
                        var theirs = await repo.GetAsync(accountId);
                        theirs.Rename("theirs");
                        await repo.UpdateAsync(theirs);
                    }, requiresNew: true);

                    account.Rename("mine");
                    await accounts.UpdateAsync(account, cancellationToken: ct);
                    return JsonDocument.Parse("{}").RootElement.Clone();
                },
            };

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var act = () => dispatcher.InvokeAsync(descriptor, JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            return (await act.Should().ThrowAsync<AbpMcpToolException>()).Which;
        });

        error.Code.Should().Be("CONFLICT");
        error.InnerException.Should().BeOfType<AbpDbConcurrencyException>();
    }

    private async Task WithUnitOfWorkAsync(Func<IRepository<LedgerAccount, Guid>, Task> body, bool requiresNew = false)
    {
        using var scope = ServiceProvider.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var uow = uowManager.Begin(requiresNew: requiresNew);
        await body(scope.ServiceProvider.GetRequiredService<IRepository<LedgerAccount, Guid>>());
        await uow.CompleteAsync();
    }

    private Task<AbpMcpToolException> InvokeThrowingAsync(Exception toThrow) =>
        RunAsAuthenticatedAsync(async services =>
        {
            var descriptor = new ToolDescriptor
            {
                Name = "Throwing_Tool",
                Description = "Throws the exception under test.",
                InputSchema = new JsonObject { ["type"] = "object" },
                ParameterNames = [],
                RequiredPermissions = [],
                Handler = (_, _) => throw toThrow,
            };

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var act = () => dispatcher.InvokeAsync(descriptor, JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            return (await act.Should().ThrowAsync<AbpMcpToolException>()).Which;
        });
}
