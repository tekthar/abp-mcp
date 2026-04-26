namespace AbpMcp.Sample.Library;

/// <summary>
/// A single checkout event. Created by <c>Loan_CheckOut</c>, settled by <c>Loan_Return</c>,
/// extended by <c>Loan_Renew</c>. The status is recomputed against the current time when read.
/// </summary>
public sealed class Loan
{
    public Guid Id { get; set; }

    public Guid EditionId { get; set; }
    public Edition? Edition { get; set; }

    public Guid MemberId { get; set; }
    public Member? Member { get; set; }

    public DateTime CheckedOutAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ReturnedAt { get; set; }

    public LoanStatus Status { get; set; } = LoanStatus.Active;
}

public enum LoanStatus
{
    Active,
    Returned,
    Overdue,
}
