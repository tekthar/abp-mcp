namespace AbpMcp.IntegrationTests.Books;

public sealed class CreateBookDto
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int Year { get; set; }
}
