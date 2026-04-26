using AbpMcp.Attributes;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Sample.Library;

/// <summary>
/// Catalog operations: search titles, inspect editions, add new titles or editions.
/// Whole class is opted in to MCP via <c>[McpTool]</c>; every public method becomes
/// a tool unless individually decorated with <c>[McpIgnore]</c>.
/// </summary>
[McpTool]
public sealed class CatalogAppService : ICatalogAppService, ITransientDependency
{
    private readonly LibraryDbContext _db;

    public CatalogAppService(LibraryDbContext db) => _db = db;

    public async Task<IReadOnlyList<TitleDto>> SearchTitlesAsync(SearchTitlesDto filter)
    {
        var q = _db.Titles.Include(t => t.Editions).AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            var needle = filter.NameContains.Trim();
            q = q.Where(t => EF.Functions.Like(t.Name, $"%{needle}%"));
        }
        if (!string.IsNullOrWhiteSpace(filter.Author))
        {
            q = q.Where(t => t.Author == filter.Author);
        }
        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            q = q.Where(t => t.Genre == filter.Genre);
        }
        if (filter.PublishedAfter.HasValue)
        {
            q = q.Where(t => t.FirstPublishedYear >= filter.PublishedAfter.Value);
        }

        var rows = await q.OrderBy(t => t.Name).Take(100).ToListAsync().ConfigureAwait(false);
        return rows.Select(MapTitle).ToList();
    }

    public async Task<TitleDto> GetTitleAsync(Guid id)
    {
        var entity = await _db.Titles
            .Include(t => t.Editions).ThenInclude(e => e.Loans)
            .FirstOrDefaultAsync(t => t.Id == id).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:TITLE_NOT_FOUND", $"No title with id {id}.");

        return MapTitle(entity);
    }

    public async Task<TitleDto> AddTitleAsync(CreateTitleDto input)
    {
        var entity = new Title
        {
            Id = Guid.NewGuid(),
            Name = input.Name.Trim(),
            Author = input.Author.Trim(),
            FirstPublishedYear = input.FirstPublishedYear,
            Genre = input.Genre.Trim(),
            Synopsis = input.Synopsis?.Trim(),
        };
        _db.Titles.Add(entity);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return MapTitle(entity);
    }

    public async Task<EditionDto> AddEditionAsync(AddEditionDto input)
    {
        var titleExists = await _db.Titles.AnyAsync(t => t.Id == input.TitleId).ConfigureAwait(false);
        if (!titleExists)
        {
            throw new BusinessException("LIBRARY:TITLE_NOT_FOUND", $"No title with id {input.TitleId}.");
        }

        var entity = new Edition
        {
            Id = Guid.NewGuid(),
            TitleId = input.TitleId,
            Format = input.Format,
            Publisher = input.Publisher.Trim(),
            ReleaseYear = input.ReleaseYear,
            Isbn = input.Isbn.Trim(),
            TotalCopies = Math.Max(1, input.TotalCopies),
        };
        _db.Editions.Add(entity);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return MapEdition(entity, activeLoans: 0);
    }

    public async Task<IReadOnlyList<EditionDto>> ListAvailableEditionsAsync(Guid titleId)
    {
        var rows = await _db.Editions
            .Where(e => e.TitleId == titleId)
            .Include(e => e.Loans)
            .AsNoTracking()
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .Select(e => MapEdition(e, e.Loans.Count(l => l.Status == LoanStatus.Active)))
            .Where(e => e.AvailableCopies > 0)
            .ToList();
    }

    private static TitleDto MapTitle(Title t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Author = t.Author,
        FirstPublishedYear = t.FirstPublishedYear,
        Genre = t.Genre,
        Synopsis = t.Synopsis,
        Editions = t.Editions
            .Select(e => MapEdition(e, e.Loans.Count(l => l.Status == LoanStatus.Active)))
            .ToList(),
    };

    private static EditionDto MapEdition(Edition e, int activeLoans) => new()
    {
        Id = e.Id,
        TitleId = e.TitleId,
        Format = e.Format,
        Publisher = e.Publisher,
        ReleaseYear = e.ReleaseYear,
        Isbn = e.Isbn,
        TotalCopies = e.TotalCopies,
        AvailableCopies = e.TotalCopies - activeLoans,
    };
}

public interface ICatalogAppService : IApplicationService
{
    Task<IReadOnlyList<TitleDto>> SearchTitlesAsync(SearchTitlesDto filter);
    Task<TitleDto> GetTitleAsync(Guid id);
    Task<TitleDto> AddTitleAsync(CreateTitleDto input);
    Task<EditionDto> AddEditionAsync(AddEditionDto input);
    Task<IReadOnlyList<EditionDto>> ListAvailableEditionsAsync(Guid titleId);
}
