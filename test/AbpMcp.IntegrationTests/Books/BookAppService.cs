using AbpMcp.Attributes;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.IntegrationTests.Books;

/// <summary>
/// The fixture service. <c>[McpTool]</c> at the class level opts the whole service
/// into MCP exposure; the dispatcher calls <see cref="CreateAsync"/> and
/// <see cref="GetListAsync"/> through DI just like any other ABP application service.
/// </summary>
[McpTool]
public sealed class BookAppService : IBookAppService, ITransientDependency
{
    private readonly BookDbContext _db;

    public BookAppService(BookDbContext db) => _db = db;

    public async Task<BookDto> CreateAsync(CreateBookDto input)
    {
        var entity = new Book
        {
            Id = Guid.NewGuid(),
            Title = input.Title,
            Author = input.Author,
            Year = input.Year,
        };

        _db.Books.Add(entity);
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return new BookDto
        {
            Id = entity.Id,
            Title = entity.Title,
            Author = entity.Author,
            Year = entity.Year,
        };
    }

    public async Task<IReadOnlyList<BookDto>> GetListAsync()
    {
        var rows = await _db.Books.AsNoTracking().ToListAsync().ConfigureAwait(false);
        return rows.Select(b => new BookDto
        {
            Id = b.Id,
            Title = b.Title,
            Author = b.Author,
            Year = b.Year,
        }).ToList();
    }
}
