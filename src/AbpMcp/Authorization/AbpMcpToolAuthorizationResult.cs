namespace AbpMcp.Authorization;

/// <summary>Outcome of <see cref="IAbpMcpToolAuthorizer.AuthorizeAsync"/>.</summary>
public sealed class AbpMcpToolAuthorizationResult
{
    private static readonly AbpMcpToolAuthorizationResult SuccessResult = new(true, null);

    private AbpMcpToolAuthorizationResult(bool succeeded, string? failureReason)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
    }

    /// <summary>True when the caller satisfies every requirement of the tool.</summary>
    public bool Succeeded { get; }

    /// <summary>Why the caller was refused. Null on success. Safe to return to the caller.</summary>
    public string? FailureReason { get; }

    /// <summary>A successful result.</summary>
    public static AbpMcpToolAuthorizationResult Success() => SuccessResult;

    /// <summary>A refused result carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">Caller-safe explanation of the missing requirement.</param>
    public static AbpMcpToolAuthorizationResult Fail(string reason) => new(false, reason);
}
