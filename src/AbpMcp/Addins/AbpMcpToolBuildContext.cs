using AbpMcp.Metadata;

namespace AbpMcp.Addins;

/// <summary>
/// The mutable working set an <see cref="IAbpMcpAddin"/> operates on. Seeded with the tools
/// discovered from ABP application services; each add-in in the pipeline may add, remove, or
/// enrich tools through the methods here. The final set becomes what the server advertises.
/// </summary>
/// <remarks>
/// Mutation goes through <see cref="AddTool"/>/<see cref="RemoveTool"/>/<see cref="Enrich"/> rather
/// than a raw list so invariants (unique tool names) hold no matter the order add-ins run in.
/// </remarks>
public sealed class AbpMcpToolBuildContext
{
    private readonly List<ToolDescriptor> _tools;
    private Dictionary<string, int> _indexByName;

    internal AbpMcpToolBuildContext(
        IEnumerable<ToolDescriptor> tools,
        AbpMcpOptions options,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
        _indexByName = BuildIndex(_tools);
        Options = options;
        Services = services;
    }

    /// <summary>The effective abp-mcp options for this build.</summary>
    public AbpMcpOptions Options { get; }

    /// <summary>
    /// The application's root service provider, for add-ins that need singletons at build time
    /// (e.g. enumerating permission definitions). Per-call work belongs in a tool handler, which
    /// receives the request scope instead.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>The current tool set, in registration order. A snapshot view — mutate via the methods below.</summary>
    public IReadOnlyList<ToolDescriptor> Tools => _tools;

    /// <summary>Find a tool by its exact name, or null if none is registered under that name.</summary>
    public ToolDescriptor? FindTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _indexByName.TryGetValue(name, out var index) ? _tools[index] : null;
    }

    /// <summary>
    /// Add a new tool. Throws <see cref="AbpMcpConfigurationException"/> if a tool with the same
    /// name already exists — name collisions are a configuration bug, not something to silently drop.
    /// </summary>
    public void AddTool(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (_indexByName.ContainsKey(tool.Name))
        {
            throw new AbpMcpConfigurationException(
                $"An add-in tried to add a tool named '{tool.Name}', but that name is already registered. " +
                "Tool names must be unique; override via [McpTool(Name = ...)] or the add-in's own naming.");
        }

        _indexByName[tool.Name] = _tools.Count;
        _tools.Add(tool);
    }

    /// <summary>Remove a tool by name. Returns true if a tool was removed.</summary>
    public bool RemoveTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_indexByName.TryGetValue(name, out var index))
        {
            return false;
        }

        _tools.RemoveAt(index);
        _indexByName = BuildIndex(_tools);
        return true;
    }

    /// <summary>
    /// Enrich an existing tool in place: <paramref name="transform"/> receives the current descriptor
    /// and returns a new one (typically <c>d =&gt; d with { ... }</c>). Returns true if the tool existed.
    /// The transform must not change the tool's <see cref="ToolDescriptor.Name"/>.
    /// </summary>
    public bool Enrich(string name, Func<ToolDescriptor, ToolDescriptor> transform)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(transform);
        if (!_indexByName.TryGetValue(name, out var index))
        {
            return false;
        }

        var updated = transform(_tools[index]);
        if (!string.Equals(updated.Name, name, StringComparison.Ordinal))
        {
            throw new AbpMcpConfigurationException(
                $"An add-in's Enrich transform changed the tool name from '{name}' to '{updated.Name}'. " +
                "Use RemoveTool + AddTool to rename a tool.");
        }

        _tools[index] = updated;
        return true;
    }

    internal IReadOnlyList<ToolDescriptor> Build() => _tools;

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<ToolDescriptor> tools)
    {
        var index = new Dictionary<string, int>(tools.Count, StringComparer.Ordinal);
        for (var i = 0; i < tools.Count; i++)
        {
            index[tools[i].Name] = i;
        }

        return index;
    }
}
