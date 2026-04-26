namespace AbpMcp.Sample.Library;

public sealed class TitleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int FirstPublishedYear { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? Synopsis { get; set; }
    public IReadOnlyList<EditionDto> Editions { get; set; } = Array.Empty<EditionDto>();
}

public sealed class EditionDto
{
    public Guid Id { get; set; }
    public Guid TitleId { get; set; }
    public Format Format { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public string Isbn { get; set; } = string.Empty;
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }
}

public sealed class CreateTitleDto
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int FirstPublishedYear { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? Synopsis { get; set; }
}

public sealed class AddEditionDto
{
    public Guid TitleId { get; set; }
    public Format Format { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public string Isbn { get; set; } = string.Empty;
    public int TotalCopies { get; set; } = 1;
}

/// <summary>
/// Filter shape for <c>Catalog_SearchTitles</c>. Each field is optional —
/// agents typically populate one at a time ("books in genre X").
/// </summary>
public sealed class SearchTitlesDto
{
    public string? NameContains { get; set; }
    public string? Author { get; set; }
    public string? Genre { get; set; }
    public int? PublishedAfter { get; set; }
}
