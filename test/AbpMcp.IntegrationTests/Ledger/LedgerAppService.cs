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

    Task<UnitOfWorkObservation> RecordWithoutTransactionAsync(string note);

    Task<UnitOfWorkObservation> GetLockedSnapshotAsync();

    Task<UnitOfWorkObservation> GetUnmanagedSnapshotAsync();
}

/// <summary>What the service saw of the ambient unit of work while it ran.</summary>
public sealed class UnitOfWorkObservation
{
    public bool HasUnitOfWork { get; set; }

    public bool IsTransactional { get; set; }

    public long Count { get; set; }
}

/// <summary>
/// Non-virtual methods, so no ABP interceptor wraps the call: whatever unit of work the call runs in
/// comes from the dispatcher. Not sealed, because <c>[UnitOfWork]</c> on a member makes ABP class-proxy
/// the type (as for any real ABP service using the attribute); the proxy cannot intercept non-virtual methods.
/// </summary>
[McpTool]
public class LedgerAppService : ILedgerAppService, ITransientDependency
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

    /// <summary>A write the developer opted out of a transaction with <c>[UnitOfWork(false)]</c>.</summary>
    [UnitOfWork(isTransactional: false)]
    public async Task<UnitOfWorkObservation> RecordWithoutTransactionAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        return Observe(count: 0);
    }

    /// <summary>A read the developer forced into a transaction with <c>[UnitOfWork(true)]</c>.</summary>
    [UnitOfWork(isTransactional: true)]
    public async Task<UnitOfWorkObservation> GetLockedSnapshotAsync() =>
        Observe(await _entries.GetCountAsync().ConfigureAwait(false));

    /// <summary>The developer turned the unit of work off entirely.</summary>
    [UnitOfWork(IsDisabled = true)]
    public async Task<UnitOfWorkObservation> GetUnmanagedSnapshotAsync() =>
        Observe(await _entries.GetCountAsync().ConfigureAwait(false));

    private UnitOfWorkObservation Observe(long count) => new()
    {
        HasUnitOfWork = _unitOfWorkManager.Current is not null,
        IsTransactional = _unitOfWorkManager.Current?.Options.IsTransactional ?? false,
        Count = count,
    };
}
