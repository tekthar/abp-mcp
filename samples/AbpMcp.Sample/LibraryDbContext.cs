using AbpMcp.Sample.Library;
using Microsoft.EntityFrameworkCore;

namespace AbpMcp.Sample;

/// <summary>
/// Plain EF Core DbContext for the sample. Uses the in-memory provider so the
/// quickstart works on a fresh clone with zero infrastructure setup. Replace
/// <c>UseInMemoryDatabase</c> with <c>UseSqlServer</c>/<c>UseNpgsql</c> in real apps.
/// </summary>
public sealed class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }

    public DbSet<Title> Titles => Set<Title>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Loan> Loans => Set<Loan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Title>(b =>
        {
            b.HasIndex(t => t.Name);
            b.HasIndex(t => t.Author);
            b.HasIndex(t => t.Genre);
            b.Property(t => t.Name).HasMaxLength(256).IsRequired();
            b.Property(t => t.Author).HasMaxLength(256).IsRequired();
            b.Property(t => t.Genre).HasMaxLength(64).IsRequired();
            b.Property(t => t.Synopsis).HasMaxLength(4096);

            b.HasMany(t => t.Editions)
                .WithOne(e => e.Title)
                .HasForeignKey(e => e.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Edition>(b =>
        {
            b.HasIndex(e => e.Isbn).IsUnique();
            b.HasIndex(e => new { e.TitleId, e.Format });
            b.Property(e => e.Publisher).HasMaxLength(128).IsRequired();
            b.Property(e => e.Isbn).HasMaxLength(32).IsRequired();
            b.Ignore(e => e.AvailableCopies); // computed in-memory, not persisted

            b.HasMany(e => e.Loans)
                .WithOne(l => l.Edition)
                .HasForeignKey(l => l.EditionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Member>(b =>
        {
            b.HasIndex(m => m.Email).IsUnique();
            b.Property(m => m.Name).HasMaxLength(128).IsRequired();
            b.Property(m => m.Email).HasMaxLength(256).IsRequired();
            b.Property(m => m.SuspensionReason).HasMaxLength(512);
        });

        modelBuilder.Entity<Loan>(b =>
        {
            b.HasIndex(l => new { l.MemberId, l.Status });
            b.HasIndex(l => l.DueAt);

            b.HasOne(l => l.Member)
                .WithMany()
                .HasForeignKey(l => l.MemberId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
