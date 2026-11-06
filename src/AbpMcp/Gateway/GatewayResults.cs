using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Dispatch;
using AbpMcp.Listing;
using AbpMcp.Metadata;
using AbpMcp.Registration;

namespace AbpMcp.Gateway;

/// <summary>
/// The JSON the gateway tools return. These shapes are a wire contract (clients such as abp-mcp-bridge parse them):
/// change them only additively.
/// </summary>
internal static class GatewayResults
{
    /// <summary>Longest one-line description <c>mcp_search_tools</c> returns.</summary>
    public const int OneLineMaxLength = 160;

    // The text block is read by a model, not embedded in HTML, so apostrophes and non-ASCII stay readable.
    private static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A gateway tool's own successful result: the payload as JSON text and as <c>structuredContent</c>.</summary>
    public static AbpMcpToolCallOutcome Success(JsonObject payload) =>
        AbpMcpToolCallOutcome.Success(payload.ToJsonString(TextOptions), JsonSerializer.SerializeToElement(payload));

    /// <summary>One <c>mcp_search_tools</c> result: no schemas, one line of description.</summary>
    public static JsonObject SearchItem(ToolDescriptor tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = OneLine(tool.Description),
        ["http_method"] = tool.HttpMethod,
        ["read_only"] = AbpMcpDispatcher.IsReadAction(tool),
        ["listing"] = ToolListingResolver.WireName(tool.Listing),
    };

    /// <summary>One <c>mcp_describe_tools</c> result, shaped as <c>tools/list</c> advertises a tool.</summary>
    public static JsonObject ProtocolTool(ToolDescriptor tool)
    {
        var item = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.InputSchema.DeepClone(),
        };

        var outputSchema = McpOutputShape.ToProtocolSchema(tool.OutputSchema);
        if (outputSchema is not null)
        {
            item["outputSchema"] = outputSchema.DeepClone();
        }

        return item;
    }

    /// <summary>
    /// One <c>mcp_call_tools</c> result. A call that ran carries <c>isError</c> and the tool's <c>structuredContent</c>,
    /// or, when it has none (errors, non-object results), its <c>content</c> blocks; never both, so the result is not
    /// repeated. A call that did not run (<paramref name="outcome"/> null) is <c>skipped</c>.
    /// </summary>
    public static JsonObject BatchItem(string name, AbpMcpToolCallOutcome? outcome)
    {
        var item = new JsonObject
        {
            ["name"] = name,
            ["status"] = outcome is null ? "skipped" : outcome.IsError ? "error" : "ok",
        };

        if (outcome is null)
        {
            return item;
        }

        item["isError"] = outcome.IsError;
        if (outcome.StructuredContent is { } structured)
        {
            item["structuredContent"] = JsonNode.Parse(structured.GetRawText());
        }
        else
        {
            item["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text });
        }

        return item;
    }

    /// <summary>
    /// The first sentence of <paramref name="description"/> on one line, cut at a word boundary to at most
    /// <see cref="OneLineMaxLength"/> characters (with an ellipsis when cut).
    /// </summary>
    public static string OneLine(string description)
    {
        var text = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var end = FirstSentenceEnd(text);
        if (end > 0)
        {
            text = text[..end];
        }

        if (text.Length <= OneLineMaxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', OneLineMaxLength - 1);
        return text[..(cut > 0 ? cut : OneLineMaxLength - 1)].TrimEnd(',', ';', ':') + "…";
    }

    /// <summary>The length of the first sentence: up to a '.', '!' or '?' followed by a space, or 0 when there is none.</summary>
    private static int FirstSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if ((text[i] is '.' or '!' or '?') && text[i + 1] == ' ')
            {
                return i + 1;
            }
        }

        return 0;
    }
}
