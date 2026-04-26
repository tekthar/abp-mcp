namespace AbpMcp.Sample.Library;

public sealed class MemberDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime MemberSince { get; set; }
    public MemberStatus Status { get; set; }
    public string? SuspensionReason { get; set; }
}

public sealed class RegisterMemberDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class SuspendMemberDto
{
    public Guid Id { get; set; }
    public string Reason { get; set; } = string.Empty;
}
