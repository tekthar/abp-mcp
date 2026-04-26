using Microsoft.EntityFrameworkCore;

namespace AbpMcp.IntegrationTests.Books;

/// <summary>
/// Plain EF Core DbContext using the in-memory provider. Keeps the test stack
/// minimal — no ABP UoW, no transaction interceptors, no migration ceremony.
/// The point of these tests is to verify the dispatcher → service → DB flow,
/// not to validate ABP's EF Core integration (that's tested by ABP itself).
/// </summary>
public sealed class BookDbContext : DbContext
{
    public BookDbContext(DbContextOptions<BookDbContext> options) : base(options)
    {
    }

    public DbSet<Book> Books => Set<Book>();
}
