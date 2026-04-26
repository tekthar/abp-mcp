namespace AbpMcp.Sample.Library;

public sealed class LoanDto
{
    public Guid Id { get; set; }
    public Guid EditionId { get; set; }
    public Guid MemberId { get; set; }
    public string TitleName { get; set; } = string.Empty;
    public Format Format { get; set; }
    public string MemberName { get; set; } = string.Empty;
    public DateTime CheckedOutAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public LoanStatus Status { get; set; }
    public bool IsOverdue { get; set; }
}

public sealed class CheckOutDto
{
    public Guid MemberId { get; set; }
    public Guid EditionId { get; set; }
    public int DurationDays { get; set; } = 14;
}

public sealed class RenewLoanDto
{
    public Guid LoanId { get; set; }
    public int AdditionalDays { get; set; } = 7;
}
