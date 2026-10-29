using AbpMcp.Attributes;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;

namespace AbpMcp.IntegrationTests.Ledger;

public interface ILedgerAppService : IApplicationService
{
    Task<int> RecordAsync(string note);

    Task RecordThenFailAsync(string note);
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

    public LedgerAppService(IRepository<LedgerEntry, Guid> entries) => _entries = entries;

    public async Task<int> RecordAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        return 1;
    }

    /// <summary>A multi-step operation that fails after its first write.</summary>
    public async Task RecordThenFailAsync(string note)
    {
        await _entries.InsertAsync(new LedgerEntry(Guid.NewGuid(), note)).ConfigureAwait(false);
        throw new BusinessException(FailureCode);
    }
}
