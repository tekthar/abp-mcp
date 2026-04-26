namespace AbpMcp.Sample.Library;

/// <summary>
/// A specific publication of a <see cref="Title"/> in one <see cref="Format"/>.
/// Different editions of the same title (Hardcover 1937 vs Paperback 2002) live as separate rows.
/// </summary>
public sealed class Edition
{
    public Guid Id { get; set; }
    public Guid TitleId { get; set; }
    public Title? Title { get; set; }

    public Format Format { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public string Isbn { get; set; } = string.Empty;
    public int TotalCopies { get; set; }

    public List<Loan> Loans { get; set; } = new();

    /// <summary>Copies physically available right now (TotalCopies minus active loans).</summary>
    public int AvailableCopies => TotalCopies - Loans.Count(l => l.Status == LoanStatus.Active);
}
