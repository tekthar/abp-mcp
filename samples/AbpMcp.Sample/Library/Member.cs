namespace AbpMcp.Sample.Library;

/// <summary>Library cardholder.</summary>
public sealed class Member
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime MemberSince { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.Active;
    public string? SuspensionReason { get; set; }
}

public enum MemberStatus
{
    Active,
    Suspended,
}
