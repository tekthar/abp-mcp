using AbpMcp.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpMcp.Registration;

/// <summary>
/// Default registry implementation.
/// </summary>
/// <remarks>
/// In v0.1 we advertise the built descriptors to the Microsoft MCP SDK through its
/// programmatic tool registration API. The exact hook depends on the SDK surface area
/// shipped in <c>ModelContextProtocol.AspNetCore</c> 1.0; keeping the bridge behind
/// this interface means the rest of the codebase is unaffected by SDK changes.
/// </remarks>
internal sealed class DynamicMcpToolRegistry : IDynamicMcpToolRegistry
{
    private readonly IApiDefinitionReader _reader;
    private readonly AbpMcpOptions _options;
    private readonly ILogger<DynamicMcpToolRegistry> _logger;

    private IReadOnlyList<ToolDescriptor> _tools = Array.Empty<ToolDescriptor>();
    private IReadOnlyDictionary<string, ToolDescriptor> _byName = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
    private bool _initialized;

    public DynamicMcpToolRegistry(
        IApiDefinitionReader reader,
        IOptions<AbpMcpOptions> options,
        ILogger<DynamicMcpToolRegistry> logger)
    {
        _reader = reader;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<ToolDescriptor> Tools => _tools;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        var descriptors = _reader.Read();

        if (descriptors.Count == 0 && _options.RequireAtLeastOneTool)
        {
            throw new AbpMcpConfigurationException(
                "abp-mcp scanned the application and found zero exposed tools. " +
                "Check that (1) your AbpAspNetCoreMvcModule is loaded, " +
                "(2) at least one IApplicationService is annotated with [McpTool], " +
                "and (3) ABP's api-definition provider is enabled. " +
                "Set AbpMcpOptions.RequireAtLeastOneTool = false to bypass this guard.");
        }

        _tools = descriptors;
        _byName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);
        _logger.LogInformation(
            "abp-mcp initialized with {ToolCount} tools: {Tools}",
            descriptors.Count,
            string.Join(", ", descriptors.Select(d => d.Name)));

        // Next: wire each descriptor into Microsoft's IMcpServer via programmatic tool registration.
        // Target API surface (tracked in csharp-sdk#317): McpServerBuilder.AddTool(ToolDescription, handler).
        // Bridge deferred until the SDK exposes a stable programmatic registration entry point;
        // in the meantime the descriptors are observable via this.Tools and can be fed through
        // WithHandler-style hooks once available.

        _initialized = true;
    }

    public bool TryGetByName(string toolName, out ToolDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        // Lazy-init defends against handler-driven access before OnApplicationInitialization fires
        // (e.g. a unit test resolving the registry directly).
        Initialize();

        if (_byName.TryGetValue(toolName, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null;
        return false;
    }
}
