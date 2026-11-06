using System.Reflection;

namespace AbpMcp.Metadata;

/// <summary>
/// Which reference-typed values an <em>output</em> schema must allow to be <c>null</c> (#86). A result
/// that serializes <c>null</c> where the schema says <c>"type": "string"</c> fails client-side validation
/// (the TypeScript MCP SDK rejects the whole call), so output schemas follow the declared nullability and
/// err toward permissive:
/// <list type="bullet">
///   <item>annotated nullable (<c>string?</c>) → nullable;</item>
///   <item>oblivious (compiled without nullable reference types) → nullable, because such code can
///     still return <c>null</c>;</item>
///   <item>annotated non-null (<c>string</c> in an NRT-enabled assembly) → unchanged.</item>
/// </list>
/// Input schemas do not use this: what an agent may send follows the parameter and <c>[Required]</c> rules.
/// </summary>
internal static class OutputNullability
{
    /// <summary>
    /// True when a value of <paramref name="type"/>, described by <paramref name="info"/>, may be null on
    /// the wire. Without nullability information (the result root) nothing is widened.
    /// </summary>
    public static bool AllowsNull(Type type, NullabilityInfo? info) =>
        info is not null &&
        !type.IsValueType &&
        info.ReadState != NullabilityState.NotNull;

    /// <summary>The nullability of a collection's elements, when it can be read from <paramref name="info"/>.</summary>
    public static NullabilityInfo? Element(NullabilityInfo? info, Type elementType)
    {
        if (info is null)
        {
            return null;
        }

        if (info.ElementType is not null)
        {
            return info.ElementType;
        }

        // List<T>, IReadOnlyList<T>, IEnumerable<T>, …: the element is the single type argument.
        return info.GenericTypeArguments.Length == 1 && info.GenericTypeArguments[0].Type == elementType
            ? info.GenericTypeArguments[0]
            : null;
    }

    /// <summary>The nullability of a dictionary's values, when it can be read from <paramref name="info"/>.</summary>
    public static NullabilityInfo? DictionaryValue(NullabilityInfo? info, Type valueType) =>
        info is not null && info.GenericTypeArguments.Length == 2 && info.GenericTypeArguments[1].Type == valueType
            ? info.GenericTypeArguments[1]
            : null;
}
