using System.Text.Json;
using AbpMcp.Dispatch;

namespace AbpMcp.Gateway;

/// <summary>
/// Reads and validates the gateway tools' arguments. Every problem is an <see cref="AbpMcpToolException"/> with
/// <see cref="AbpMcpErrorCodes.ValidationError"/> naming the argument, raised before anything runs.
/// </summary>
internal static class GatewayArguments
{
    /// <summary>One call of <c>mcp_call_tool</c> or <c>mcp_call_tools</c>: the tool name and its arguments object.</summary>
    internal readonly record struct ToolCall(string Name, JsonElement Arguments);

    private static readonly JsonElement EmptyObject = ParseEmptyObject();

    /// <summary>The non-blank string <paramref name="property"/>, trimmed.</summary>
    public static string RequiredString(JsonElement arguments, string property, string path = "")
    {
        if (!TryGet(arguments, property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"'{path}{property}' is required and must be a non-empty string.");
        }

        return value.GetString()!.Trim();
    }

    /// <summary><c>limit</c>: absent means the default; any integer is clamped to 1..<see cref="GatewayToolDefinitions.MaxSearchLimit"/>.</summary>
    public static int Limit(JsonElement arguments)
    {
        if (!TryGet(arguments, "limit", out var value))
        {
            return GatewayToolDefinitions.DefaultSearchLimit;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var limit))
        {
            throw Invalid("'limit' must be an integer.");
        }

        return (int)Math.Clamp(limit, 1, GatewayToolDefinitions.MaxSearchLimit);
    }

    /// <summary><c>names</c>: 1 to <see cref="GatewayToolDefinitions.MaxDescribeNames"/> tool names, duplicates removed, order kept.</summary>
    public static IReadOnlyList<string> Names(JsonElement arguments)
    {
        if (!TryGet(arguments, "names", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("'names' is required and must be an array of tool names.");
        }

        var names = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Invalid("Every entry of 'names' must be a non-empty string.");
            }

            names.Add(item.GetString()!.Trim());
        }

        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count is 0 or > GatewayToolDefinitions.MaxDescribeNames)
        {
            throw Invalid($"'names' must hold 1 to {GatewayToolDefinitions.MaxDescribeNames} tool names.");
        }

        return distinct;
    }

    /// <summary>The <c>name</c> and <c>arguments</c> of one call; a gateway tool's name is refused.</summary>
    public static ToolCall Call(JsonElement call, string path, string via)
    {
        if (call.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"'{path.TrimEnd('.')}' must be an object with 'name' and 'arguments'.");
        }

        var name = RequiredString(call, "name", path);
        if (AbpMcpGatewayToolNames.IsGatewayTool(name))
        {
            throw Invalid($"'{name}' is a gateway tool: call it directly, not through {via}.");
        }

        if (!TryGet(call, "arguments", out var arguments))
        {
            return new ToolCall(name, EmptyObject);
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"'{path}arguments' must be a JSON object matching the tool's input schema.");
        }

        return new ToolCall(name, arguments.Clone());
    }

    /// <summary><c>calls</c>: 1 to <see cref="GatewayToolDefinitions.MaxBatchCalls"/> valid calls, all checked before any runs.</summary>
    public static IReadOnlyList<ToolCall> Calls(JsonElement arguments)
    {
        if (!TryGet(arguments, "calls", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("'calls' is required and must be an array of { name, arguments } objects.");
        }

        var count = value.GetArrayLength();
        if (count is 0 or > GatewayToolDefinitions.MaxBatchCalls)
        {
            throw Invalid($"'calls' must hold 1 to {GatewayToolDefinitions.MaxBatchCalls} calls; it holds {count}.");
        }

        return value.EnumerateArray()
            .Select((call, index) => Call(call, $"calls[{index}].", AbpMcpGatewayToolNames.CallMany))
            .ToList();
    }

    /// <summary><c>mode</c>: true for <c>sequential</c> (the default), false for <c>parallel</c>.</summary>
    public static bool IsSequential(JsonElement arguments)
    {
        if (!TryGet(arguments, "mode", out var value))
        {
            return true;
        }

        var mode = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.Equals(mode, "sequential", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, "parallel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw Invalid("'mode' must be \"sequential\" or \"parallel\".");
    }

    /// <summary>The property, unless it is absent or JSON null.</summary>
    private static bool TryGet(JsonElement arguments, string property, out JsonElement value)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(property, out value) &&
            value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static AbpMcpToolException Invalid(string message) => new(AbpMcpErrorCodes.ValidationError, message);

    private static JsonElement ParseEmptyObject()
    {
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}
