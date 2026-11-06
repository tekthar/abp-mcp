using System.Text.Json;

namespace AbpMcp.Dispatch;

/// <summary>
/// The result of one tool call, free of MCP SDK types: <c>AbpMcpHandlerWiring</c> turns it into the protocol's
/// <c>CallToolResult</c>, and <c>mcp_call_tools</c> reports it per call.
/// </summary>
/// <param name="IsError">True for a coded error (<c>[CODE] message</c> in <paramref name="Text"/>).</param>
/// <param name="Text">The text content block: the result's JSON, or <c>[CODE] message</c> for an error.</param>
/// <param name="StructuredContent">The <c>structuredContent</c> object, or null when there is none.</param>
internal sealed record AbpMcpToolCallOutcome(bool IsError, string Text, JsonElement? StructuredContent)
{
    /// <summary>A successful call.</summary>
    public static AbpMcpToolCallOutcome Success(string text, JsonElement? structuredContent) => new(false, text, structuredContent);

    /// <summary>A failed call, rendered <c>[code] message</c> as every tool error is.</summary>
    public static AbpMcpToolCallOutcome Error(string code, string message) => new(true, $"[{code}] {message}", null);
}
