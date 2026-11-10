using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using AbpMcp.Metadata;

namespace AbpMcp.Gateway;

/// <summary>
/// What <c>tools/list</c> advertises for the four gateway tools, and when. Kept short on purpose: the gateway exists
/// to save the agent's context, so its own descriptions and schemas must stay small. No output schemas, for the same
/// reason; each description states the result's shape instead.
/// </summary>
internal static class GatewayToolDefinitions
{
    /// <summary>Default number of <c>mcp_search_tools</c> results.</summary>
    public const int DefaultSearchLimit = 10;

    /// <summary>Most <c>mcp_search_tools</c> results; a larger <c>limit</c> is clamped to it.</summary>
    public const int MaxSearchLimit = 50;

    /// <summary>Longest <c>query</c> <c>mcp_search_tools</c> accepts: a few keywords, never a document.</summary>
    public const int MaxSearchQueryLength = 512;

    /// <summary>Most names one <c>mcp_describe_tools</c> accepts.</summary>
    public const int MaxDescribeNames = 20;

    /// <summary>Most calls one <c>mcp_call_tools</c> accepts.</summary>
    public const int MaxBatchCalls = 20;

    /// <summary>The gateway is on when at least one registered tool is on demand; it is then listed for every caller.</summary>
    public static bool IsEnabled(IEnumerable<ToolDescriptor> registryTools) =>
        registryTools.Any(tool => tool.Listing == McpToolListing.OnDemand);

    /// <summary>The four gateway tools, in <see cref="AbpMcpGatewayToolNames.All"/> order.</summary>
    public static IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        Define(
            AbpMcpGatewayToolNames.Search,
            "Find tools on this server by keyword, including on-demand tools that are not in your tool list. Returns the " +
            "best matches first: name, a one-line description, http_method, read_only, and listing (listed or on_demand). " +
            $"Then read schemas with {AbpMcpGatewayToolNames.Describe} and call with {AbpMcpGatewayToolNames.Call} or {AbpMcpGatewayToolNames.CallMany}.",
            new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = MaxSearchQueryLength,
                    ["description"] = "Keywords, such as \"overdue loans\" or \"suspend member\". Matched against tool names, services and descriptions.",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = MaxSearchLimit,
                    ["description"] = $"Most results to return. Default {DefaultSearchLimit}.",
                },
            },
            "query"),
        Define(
            AbpMcpGatewayToolNames.Describe,
            "Get the full description and the input and output JSON schemas (inputSchema, outputSchema) of tools found with " +
            $"{AbpMcpGatewayToolNames.Search}. Names that do not exist or that you cannot use are returned in not_found.",
            new JsonObject
            {
                ["names"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["minItems"] = 1,
                    ["maxItems"] = MaxDescribeNames,
                    ["description"] = "Exact tool names.",
                },
            },
            "names"),
        Define(
            AbpMcpGatewayToolNames.Call,
            "Call one tool by name, including on-demand tools that are not in your tool list, with arguments matching its " +
            $"input schema (see {AbpMcpGatewayToolNames.Describe}). Returns the tool's own result, as calling it directly would.",
            CallProperties(),
            "name"),
        Define(
            AbpMcpGatewayToolNames.CallMany,
            $"Call up to {MaxBatchCalls} tools in one request. mode \"sequential\" (default) runs the calls in order and stops at " +
            "the first error, reporting the rest as skipped; \"parallel\" runs every call whatever the others return. Each " +
            "call is permission-checked and has its own unit of work. Returns results in call order: name, status (ok, " +
            "error or skipped), isError, and the tool's structuredContent, or its content when it has none.",
            new JsonObject
            {
                ["calls"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = MaxBatchCalls,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = CallProperties(),
                        ["required"] = new JsonArray("name"),
                    },
                },
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("sequential", "parallel"),
                    ["description"] = "Default sequential.",
                },
            },
            "calls"),
    ];

    private static JsonObject CallProperties() => new()
    {
        ["name"] = Described("string", "Exact tool name."),
        ["arguments"] = Described("object", "The tool's arguments, matching its input schema."),
    };

    private static JsonObject Described(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static ToolDescriptor Define(string name, string description, JsonObject properties, string required) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required),
        },
        ParameterNames = Array.Empty<string>(),
        RequiredPermissions = Array.Empty<string>(),
    };
}
