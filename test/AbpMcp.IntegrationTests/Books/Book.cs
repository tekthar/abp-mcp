namespace AbpMcp.IntegrationTests.Books;

/// <summary>
/// Test fixture entity. Mirrors the shape of a typical ABP CRUD entity without
/// dragging in <c>Volo.Abp.Domain.Entities</c> dependencies. The "DB" we verify
/// against is an EF Core in-memory <see cref="BookDbContext"/>.
/// </summary>
public sealed class Book
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int Year { get; set; }
}
