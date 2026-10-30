using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace AbpMcp.IntegrationTests.Ledger;

public sealed class LedgerDbContext : AbpDbContext<LedgerDbContext>
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options)
        : base(options)
    {
    }

    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
}
