using Volo.Abp.Domain.Entities;

namespace AbpMcp.IntegrationTests.Ledger;

/// <summary>
/// ABP entity persisted through ABP's EF Core repository, so writes take part in ABP's unit of
/// work (saved on <c>CompleteAsync</c>) the way a real app service's writes do. The Books fixture
/// deliberately bypasses ABP's UoW; this one exists to test it.
/// </summary>
public sealed class LedgerEntry : Entity<Guid>
{
    private LedgerEntry()
    {
    }

    public LedgerEntry(Guid id, string note)
        : base(id)
    {
        Note = note;
    }

    public string Note { get; private set; } = string.Empty;
}
