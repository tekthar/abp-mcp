namespace AbpMcp.Sample.Library;

/// <summary>
/// A work in the catalog (e.g. "The Hobbit"). One Title can ship as multiple
/// <see cref="Edition"/>s — a Hardcover, a Paperback, an Audiobook, etc.
/// </summary>
public sealed class Title
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int FirstPublishedYear { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? Synopsis { get; set; }

    public List<Edition> Editions { get; set; } = new();
}
