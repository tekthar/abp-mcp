using Volo.Abp.Application.Services;

namespace AbpMcp.IntegrationTests.Books;

public interface IBookAppService : IApplicationService
{
    Task<BookDto> CreateAsync(CreateBookDto input);
    Task<IReadOnlyList<BookDto>> GetListAsync();
}
