using AbpMcp.Metadata;
using Microsoft.AspNetCore.Http;

namespace AbpMcp.Authorization;

/// <summary>
/// Decides whether the current caller may see and invoke a tool. The single authorization gate
/// shared by <c>tools/list</c> filtering and the <c>tools/call</c> pre-invoke check, so the two
/// paths can never disagree.
/// </summary>
public interface IAbpMcpToolAuthorizer
{
    /// <summary>
    /// Evaluate <see cref="ToolDescriptor.RequiredPermissions"/> and <see cref="ToolDescriptor.AuthorizeData"/>
    /// for the caller of <paramref name="httpContext"/>.
    /// </summary>
    /// <param name="descriptor">The tool being listed or invoked.</param>
    /// <param name="httpContext">The current request. Null means no caller identity is available; any tool with requirements is then refused.</param>
    /// <returns>The authorization outcome, with a caller-safe reason when refused.</returns>
    Task<AbpMcpToolAuthorizationResult> AuthorizeAsync(ToolDescriptor descriptor, HttpContext? httpContext);
}
