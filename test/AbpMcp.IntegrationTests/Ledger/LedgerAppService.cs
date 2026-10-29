using AbpMcp.Attributes;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace AbpMcp.IntegrationTests.Ledger;

public interface ILedgerAppService : IApplicationService
{
    Task<UnitOfWorkObservation> RecordAsync(string note);

    Task RecordThenFailAsync(string note);

    Task<UnitOfWorkObservation> GetSnapshotAsync();
}

/// <summary>What the service saw of the ambient unit of work while it ran.</summary>
public sealed class UnitOfWorkObservation
{
    public bool HasUnitOfWork { get; set; }

    public bool IsTransactional { get; set; }

    public long Count { get; set; }
}

/// <summary>
/// Sealed with non-virtual methods, like the Books fixture, so no ABP interceptor wraps the call:
/// whatever unit of work the call runs in comes from the dispatcher.
/// </summary>
[McpTool]
public sealed class LedgerAppService : ILedgerAppService, ITransientDependency
{
    public const string FailureCode = "LEDGER:FAILED_AFTER_WRITE";

    private readonly IRepository<LedgerEntry, Guid> _entries;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public LedgerAppService(IRepository<LedgerEntry, Guid> entries, IUnitOfWorkManager unitOfWorkManager)
    {
        _entries = entries;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public async Task<UnitOfWorkObservation> RecordAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        return Observe(count: 0);
    }

    /// <summary>A multi-step operation that fails after its first write.</summary>
    public async Task RecordThenFailAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        throw new BusinessException(FailureCode);
    }

    /// <summary>A read: maps to GET by ABP's naming convention.</summary>
    public async Task<UnitOfWorkObservation> GetSnapshotAsync() =>
        Observe(await _entries.GetCountAsync().ConfigureAwait(false));

    private UnitOfWorkObservation Observe(long count) => new()
    {
        HasUnitOfWork = _unitOfWorkManager.Current is not null,
        IsTransactional = _unitOfWorkManager.Current?.Options.IsTransactional ?? false,
        Count = count,
    };
}
