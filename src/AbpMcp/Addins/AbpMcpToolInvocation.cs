using System.Security.Claims;
using System.Text.Json;
using AbpMcp.Metadata;

namespace AbpMcp.Addins;

/// <summary>
/// The context handed to a dynamic tool's <see cref="AbpMcpToolHandler"/> when an agent calls it.
/// Everything a handler needs to do real work: the raw arguments, the caller's identity, and the
/// per-request service scope (resolve application services, repositories, <c>ICurrentUser</c>, etc.
/// from here so the call runs inside the same unit of work and tenant context as any other request).
/// </summary>
public sealed class AbpMcpToolInvocation
{
    /// <summary>The descriptor of the tool being invoked.</summary>
    public required ToolDescriptor Descriptor { get; init; }

    /// <summary>The arguments object supplied by the agent, shaped by the tool's <see cref="ToolDescriptor.InputSchema"/>.</summary>
    public required JsonElement Arguments { get; init; }

    /// <summary>The request-scoped service provider. Resolve services from here, never from the root.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The authenticated caller. The dispatcher has already enforced <see cref="ToolDescriptor.RequiredPermissions"/> before this runs.</summary>
    public required ClaimsPrincipal User { get; init; }
}

/// <summary>
/// Handler for a dynamic tool contributed by an <see cref="IAbpMcpAddin"/>. Returns the tool's
/// structured JSON result. Throw <see cref="Dispatch.AbpMcpToolException"/> to return a coded error;
/// any other exception is mapped by the dispatcher the same way a service method's would be.
/// </summary>
public delegate Task<JsonElement> AbpMcpToolHandler(AbpMcpToolInvocation invocation, CancellationToken cancellationToken);
