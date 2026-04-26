using AbpMcp.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpMcp.Registration;

/// <summary>
/// Default registry implementation.
/// </summary>
/// <remarks>
/// Descriptors are surfaced to Microsoft's MCP SDK through the ListTools/CallTool handlers
/// wired in <see cref="AbpMcpHandlerWiring"/>. Keeping the registry behind this interface
/// means the rest of the codebase is unaffected if the SDK exposes a separate programmatic
/// tool-registration API later (tracked upstream in <c>modelcontextprotocol/csharp-sdk#317</c>);
/// at that point switching the bridge implementation is a one-file change.
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
