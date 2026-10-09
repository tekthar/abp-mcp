using Volo.Abp.Application.Services;

namespace AbpMcp.IntegrationTests.Books;

public interface IBookAppService : IApplicationService
{
    /// <summary>Creates a new book in the catalog.</summary>
    /// <param name="input">The book to create.</param>
    Task<BookDto> CreateAsync(CreateBookDto input);

    Task<IReadOnlyList<BookDto>> GetListAsync();
}
