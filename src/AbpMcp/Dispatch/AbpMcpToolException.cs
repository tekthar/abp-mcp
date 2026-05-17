namespace AbpMcp.Dispatch;

/// <summary>
/// Translated tool-invocation failure that should be returned to the MCP client
/// as a structured tool error. Carries a stable error code the agent can branch on.
/// </summary>
public sealed class AbpMcpToolException : Exception
{
    /// <summary>Stable error code (see <see cref="AbpMcpErrorCodes"/>).</summary>
    public string Code { get; }

    /// <inheritdoc/>
    public AbpMcpToolException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <inheritdoc/>
    public AbpMcpToolException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}
