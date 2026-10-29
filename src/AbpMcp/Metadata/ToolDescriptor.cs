using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using Microsoft.AspNetCore.Authorization;

namespace AbpMcp.Metadata;

/// <summary>
/// An MCP tool description. Most tools are resolved from an ABP application service method
/// (<see cref="ServiceType"/> + <see cref="Method"/>); an add-in may also contribute a
/// <em>dynamic</em> tool backed by a <see cref="Handler"/> delegate instead.
/// Consumed by the dispatcher to route tool calls and advertise the tool to clients.
/// </summary>
/// <remarks>
/// Tool descriptors are immutable and built once at startup. For method-backed tools all
/// reflection happens here so the dispatcher hot path can use <see cref="MethodInfo"/> directly
/// without scanning. Enrich a descriptor from an add-in with a <c>with</c> expression.
/// </remarks>
public sealed record ToolDescriptor
{
    /// <summary>MCP tool name, after prefix and naming convention have been applied.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description advertised to the agent. Pulled from <c>[McpTool(Description=...)]</c> or a mechanical fallback.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The application service type that implements a method-backed tool, resolved from DI at
    /// invocation time. Null for a dynamic tool contributed by an add-in (see <see cref="Handler"/>).
    /// </summary>
    public Type? ServiceType { get; init; }

    /// <summary>
    /// The specific method to invoke for a method-backed tool. Null for a dynamic tool
    /// contributed by an add-in (see <see cref="Handler"/>).
    /// </summary>
    public MethodInfo? Method { get; init; }

    /// <summary>
    /// The handler for a dynamic tool contributed by an add-in. When set, the dispatcher runs this
    /// instead of resolving and invoking an ABP service method — the way an add-in adds a tool that
    /// is not a one-to-one wrapper over an application-service method (meta-tools, composite tools).
    /// </summary>
    public AbpMcpToolHandler? Handler { get; init; }

    /// <summary>JSON Schema for the tool's input object.</summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>
    /// Optional JSON Schema for the tool's structured result. Null when no output schema is advertised.
    /// The built-in output-schema add-in populates this from method return types; add-ins may set it too.
    /// </summary>
    public JsonObject? OutputSchema { get; init; }

    /// <summary>Ordered names of the parameters the method expects. Used to map JSON input into positional args. Empty for dynamic tools.</summary>
    public required IReadOnlyList<string> ParameterNames { get; init; }

    /// <summary>
    /// Permission (policy) names required to call this tool; every one must be granted. For a
    /// method-backed tool this is the union of the <c>[Authorize(Policy)]</c> names declared on the
    /// method and on its service class. Add-ins may add to it. Empty means no named permission is
    /// required — <see cref="AuthorizeData"/> can still demand an authenticated user, roles, or schemes.
    /// </summary>
    public required IReadOnlyList<string> RequiredPermissions { get; init; }

    /// <summary>
    /// The complete <c>[Authorize]</c> data (policies, roles, authentication schemes, and policy-less
    /// authenticated-only markers) declared on the method and its service class, combined the way
    /// ASP.NET Core combines them for the parallel HTTP endpoint. Empty when the method is
    /// <c>[AllowAnonymous]</c> or declares nothing. Enforced alongside <see cref="RequiredPermissions"/>
    /// on both <c>tools/list</c> and <c>tools/call</c>.
    /// </summary>
    public IReadOnlyList<IAuthorizeData> AuthorizeData { get; init; } = Array.Empty<IAuthorizeData>();
}
