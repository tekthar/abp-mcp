using System.Reflection;

namespace AbpMcp;

/// <summary>
/// Configuration for the abp-mcp module. Mirrors the shape of ABP's
/// <c>AbpAspNetCoreMvcOptions.ConventionalControllers</c> so an ABP developer
/// reaches for familiar knobs.
/// </summary>
public sealed class AbpMcpOptions
{
    /// <summary>
    /// HTTP path where the MCP endpoint is mounted. Default <c>/mcp</c>.
    /// </summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// If true, the MCP endpoint allows anonymous requests. Default <c>false</c>.
    /// Enterprise apps should leave this disabled; agents authenticate with the same
    /// bearer tokens used by the rest of the application.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// If true (default), startup fails loudly when the scan produces zero exposed tools.
    /// This catches misconfiguration (module not loaded, no <c>[McpTool]</c> anywhere,
    /// ABP's api-definition endpoint disabled) before it silently masquerades as a working server.
    /// </summary>
    public bool RequireAtLeastOneTool { get; set; } = true;

    /// <summary>
    /// Optional tool name prefix applied to every generated tool (e.g. <c>"myapp_"</c>).
    /// Helps disambiguate when multiple MCP servers are attached to one agent.
    /// </summary>
    public string? ToolNamePrefix { get; set; }

    /// <summary>
    /// Server name advertised to MCP clients in the <c>initialize</c> response.
    /// Defaults to the hosting assembly's product name or <c>"abp-mcp"</c>.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Server version advertised to MCP clients. Defaults to the hosting assembly's informational version.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Per-tool kill switch. Adding a name here removes the tool from <c>tools/list</c> and
    /// rejects <c>tools/call</c> at the dispatcher boundary. Hot-reload via <c>IOptionsMonitor</c>;
    /// no redeploy required for an emergency disable.
    /// </summary>
    /// <remarks>
    /// In v0.2 this is intended to be backed by ABP's Settings Management for cross-instance
    /// coordination. v0.1 keeps it in-process, which is enough for single-host deployments.
    /// </remarks>
    public ISet<string> DisabledTools { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Assemblies to scan for <c>[McpTool]</c>-tagged services. Mirrors ABP's
    /// <c>options.ConventionalControllers.Create(typeof(MyModule).Assembly, ...)</c> shape.
    /// </summary>
    /// <example>
    /// <code>
    /// Configure&lt;AbpMcpOptions&gt;(opts =>
    /// {
    ///     opts.ExposedAssemblies.Create(typeof(BookStoreApplicationModule).Assembly);
    /// });
    /// </code>
    /// </example>
    public ExposedAssembliesCollection ExposedAssemblies { get; } = new();

    /// <summary>
    /// Pluggable tool-name normalizer. The default strips <c>AppService</c>/<c>ApplicationService</c>/<c>Service</c>
    /// suffixes from the type name and <c>Async</c> suffix from the method, joining with <c>_</c>,
    /// then prepends <see cref="ToolNamePrefix"/>.
    /// </summary>
    /// <remarks>
    /// Returning a string already containing only ASCII letters, digits, and underscores is required
    /// by the MCP spec. The normalizer is responsible for compliance.
    /// </remarks>
    public Func<ToolNamingContext, string> ToolNameNormalizer { get; set; } =
        DefaultToolNameNormalizer.Normalize;
}

/// <summary>
/// Context passed to <see cref="AbpMcpOptions.ToolNameNormalizer"/>.
/// </summary>
public sealed class ToolNamingContext
{
    /// <summary>The application service type defining the method.</summary>
    public required Type ServiceType { get; init; }

    /// <summary>The method being exposed.</summary>
    public required MethodInfo Method { get; init; }

    /// <summary>The configured <see cref="AbpMcpOptions.ToolNamePrefix"/>, if any.</summary>
    public string? ConfiguredPrefix { get; init; }
}

/// <summary>
/// Built-in tool name normalizer. Public so callers can compose with it
/// (call default first, then post-process).
/// </summary>
public static class DefaultToolNameNormalizer
{
    private static readonly string[] MethodSuffixes = ["Async"];
    private static readonly string[] ServiceSuffixes = ["AppService", "ApplicationService", "Service"];

    /// <summary>Apply the default naming rule.</summary>
    public static string Normalize(ToolNamingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var service = StripSuffix(context.ServiceType.Name, ServiceSuffixes);
        var method = StripSuffix(context.Method.Name, MethodSuffixes);
        var raw = $"{service}_{method}";

        return string.IsNullOrEmpty(context.ConfiguredPrefix)
            ? raw
            : context.ConfiguredPrefix + raw;
    }

    private static string StripSuffix(string value, IReadOnlyList<string> suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }
}

/// <summary>
/// Collection of assemblies to scan for MCP-eligible services.
/// Shaped after ABP's <c>ConventionalControllerSettings</c>.
/// </summary>
public sealed class ExposedAssembliesCollection : IEnumerable<ExposedAssemblyEntry>
{
    private readonly List<ExposedAssemblyEntry> _entries = new();

    /// <summary>Number of registered entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Registers <paramref name="assembly"/> for scanning. Returns the entry so callers can
    /// configure per-assembly options once we expose them (predicates, glob filters in v0.2).
    /// </summary>
    public ExposedAssemblyEntry Create(Assembly assembly, Action<ExposedAssemblyEntry>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var entry = new ExposedAssemblyEntry(assembly);
        configure?.Invoke(entry);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>True when no assemblies have been registered (the reader will fall back to scanning all loaded ABP modules).</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <inheritdoc/>
    public IEnumerator<ExposedAssemblyEntry> GetEnumerator() => _entries.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// One registered scan target. Per-assembly knobs (predicates, glob filters) will hang here in v0.2.
/// </summary>
public sealed class ExposedAssemblyEntry
{
    /// <summary>The assembly to scan.</summary>
    public Assembly Assembly { get; }

    /// <summary>Construct an entry for the given assembly.</summary>
    public ExposedAssemblyEntry(Assembly assembly) => Assembly = assembly;
}
