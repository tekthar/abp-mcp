using AbpMcp.Attributes;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Fixtures.Application;

/// <summary>A quote of the day.</summary>
public sealed class QuoteDto
{
    public string Text { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;
}

public interface IQuoteAppService : IApplicationService
{
    Task<QuoteDto> GetOfTheDayAsync();
}

/// <summary>
/// An ordinary, conventionally remoted app service tagged through AbpMcp.Abstractions. It must become the
/// tool <c>Quote_GetOfTheDay</c> without its assembly ever referencing AbpMcp (#98).
/// </summary>
[McpTool]
public sealed class QuoteAppService : IQuoteAppService, ITransientDependency
{
    public Task<QuoteDto> GetOfTheDayAsync() =>
        Task.FromResult(new QuoteDto { Text = "A reader lives a thousand lives.", Author = "George R. R. Martin" });
}
