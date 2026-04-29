namespace AbpMcp.Dispatch;

/// <summary>
/// Stable error code constants emitted by the MCP dispatcher and handler wiring.
/// Code values are part of the wire contract that MCP clients (agents) branch on,
/// so they must stay byte-stable across releases.
/// </summary>
public static class AbpMcpErrorCodes
{
    /// <summary>Input failed validation (DTO required field missing, value out of range, etc.).</summary>
    public const string ValidationError = "VALIDATION_ERROR";

    /// <summary>Caller is authenticated but lacks the required ABP permission.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>Tool name is not registered (or was removed).</summary>
    public const string UnknownTool = "UNKNOWN_TOOL";

    /// <summary>Tool is administratively disabled via AbpMcpOptions.DisabledTools.</summary>
    public const string Disabled = "DISABLED";

    /// <summary>The underlying ABP service raised a BusinessException; Code is the ABP code if present.</summary>
    public const string BusinessError = "BUSINESS_ERROR";

    /// <summary>Request was cancelled (client disconnected, timeout, etc.).</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Unhandled exception. No stack trace is leaked to the agent.</summary>
    public const string Internal = "INTERNAL";
}
