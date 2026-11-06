using Volo.Abp.Domain.Entities;

namespace AbpMcp.IntegrationTests.Ledger;

/// <summary>
/// ABP aggregate root, so it carries a <c>ConcurrencyStamp</c> that ABP's EF Core layer checks on update:
/// two writers that read the same version collide, as two editors do in a real app.
/// </summary>
public sealed class LedgerAccount : AggregateRoot<Guid>
{
    private LedgerAccount()
    {
    }

    public LedgerAccount(Guid id, string name)
        : base(id)
    {
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

    public void Rename(string name) => Name = name;
}
