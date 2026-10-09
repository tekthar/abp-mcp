namespace AbpMcp.Addins;

/// <summary>
/// A dynamic add-in that participates in building the MCP tool set. Add-ins are the extension
/// point through which a host — or a third-party module — grows and empowers its MCP server:
/// they run as an ordered pipeline over the tools discovered from ABP application services and may
/// enrich existing tools (descriptions, schemas, output schemas, constraints) or contribute
/// entirely new dynamic tools that are not one-to-one wrappers over a service method.
/// </summary>
/// <remarks>
/// Register an add-in in DI — <c>context.Services.AddSingleton&lt;IAbpMcpAddin, MyAddin&gt;()</c> —
/// and it is picked up automatically. Any number of add-ins may be registered; they run in ascending
/// <see cref="Order"/>. The recommended ordering bands mirror the pipeline stages:
/// <list type="bullet">
///   <item><c>&lt; 100</c> — discovery (add or remove tools).</item>
///   <item><c>100–499</c> — metadata (names, descriptions, permissions).</item>
///   <item><c>500–899</c> — schema (input/output schema shaping).</item>
///   <item><c>≥ 900</c> — security (final visibility and hardening passes).</item>
/// </list>
/// Add-ins run once, at startup, before the server serves its first <c>tools/list</c>. They must be
/// deterministic and side-effect free with respect to the host — do the per-call work in a dynamic
/// tool's <see cref="AbpMcpToolHandler"/>, not in <see cref="Contribute"/>.
/// </remarks>
public interface IAbpMcpAddin
{
    /// <summary>Ascending execution order within the add-in pipeline. See the ordering bands in the type remarks.</summary>
    int Order { get; }

    /// <summary>Contribute to the tool set: add, remove, or enrich tools via <paramref name="context"/>.</summary>
    void Contribute(AbpMcpToolBuildContext context);
}
