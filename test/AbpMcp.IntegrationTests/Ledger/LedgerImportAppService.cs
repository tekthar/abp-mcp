using AbpMcp.Attributes;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace AbpMcp.IntegrationTests.Ledger;

public interface ILedgerImportAppService : IApplicationService
{
    Task<UnitOfWorkObservation> ImportAsync(string note);
}

/// <summary>
/// Class-level <c>[UnitOfWork(false)]</c>: applies to every method that does not declare its own.
/// Not sealed and non-virtual, for the same reason as <see cref="LedgerAppService"/>.
/// </summary>
[McpTool]
[UnitOfWork(isTransactional: false)]
public class LedgerImportAppService : ILedgerImportAppService, ITransientDependency
{
    private readonly IRepository<LedgerEntry, Guid> _entries;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public LedgerImportAppService(IRepository<LedgerEntry, Guid> entries, IUnitOfWorkManager unitOfWorkManager)
    {
        _entries = entries;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public async Task<UnitOfWorkObservation> ImportAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        return new UnitOfWorkObservation
        {
            HasUnitOfWork = _unitOfWorkManager.Current is not null,
            IsTransactional = _unitOfWorkManager.Current?.Options.IsTransactional ?? false,
        };
    }
}
