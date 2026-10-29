using System.ComponentModel.DataAnnotations;

namespace AbpMcp.IntegrationTests.Books;

public sealed class CreateBookDto
{
    [Required]
    [StringLength(128)]
    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    [Range(1, 3000)]
    public int Year { get; set; }

    public BookGenre Genre { get; set; }
}
