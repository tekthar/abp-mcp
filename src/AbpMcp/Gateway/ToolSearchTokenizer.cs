namespace AbpMcp.Gateway;

/// <summary>
/// Splits tool names, service names, descriptions and search queries into comparable tokens for
/// <see cref="ToolSearchIndex"/>: alphanumeric runs (so <c>_</c>, <c>.</c>, <c>-</c> and spaces separate), then camelCase,
/// PascalCase and letter/digit boundaries; lowercased, with a light plural stem. No dictionaries, no embeddings.
/// </summary>
internal static class ToolSearchTokenizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "its",
        "of", "on", "or", "that", "the", "this", "to", "with",
    };

    /// <summary>
    /// The tokens of a searchable field. Each run contributes its parts and, when it has several, itself as one
    /// word, so <c>CheckOut</c> matches both "check out" and "checkout".
    /// </summary>
    public static HashSet<string> FieldTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        foreach (var run in Runs(text))
        {
            var parts = SplitCase(run);
            foreach (var part in parts)
            {
                AddToken(tokens, part);
            }

            if (parts.Count > 1)
            {
                AddToken(tokens, run);
            }
        }

        return tokens;
    }

    /// <summary>
    /// The distinct tokens of a query, in order. Stop words are dropped unless nothing else is left.
    /// </summary>
    public static IReadOnlyList<string> QueryTokens(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var all = Runs(query).SelectMany(SplitCase).Select(Normalize).Where(t => t.Length > 1).Distinct(StringComparer.Ordinal).ToList();
        var meaningful = all.Where(t => !StopWords.Contains(t)).ToList();
        return meaningful.Count > 0 ? meaningful : all;
    }

    /// <summary>The text lowercased with everything but letters and digits removed: <c>Loan_CheckOut</c> → <c>loancheckout</c>.</summary>
    public static string Compact(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void AddToken(HashSet<string> tokens, string raw)
    {
        var token = Normalize(raw);
        if (token.Length > 1 && !StopWords.Contains(token))
        {
            tokens.Add(token);
        }
    }

    private static string Normalize(string raw) => Stem(raw.ToLowerInvariant());

    /// <summary>Plural to singular, applied the same way to fields and queries: <c>loans</c> → <c>loan</c>, <c>entries</c> → <c>entry</c>.</summary>
    internal static string Stem(string token)
    {
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
        {
            return token[..^3] + "y";
        }

        if (token.Length > 3 && token[^1] == 's' && !token.EndsWith("ss", StringComparison.Ordinal) &&
            !token.EndsWith("us", StringComparison.Ordinal) && !token.EndsWith("is", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
    }

    private static IEnumerable<string> Runs(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inRun = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inRun && start < 0)
            {
                start = i;
            }
            else if (!inRun && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }

    /// <summary><c>getHTTPResponse2Fast</c> → <c>get</c>, <c>HTTP</c>, <c>Response</c>, <c>2</c>, <c>Fast</c>.</summary>
    internal static IReadOnlyList<string> SplitCase(string run)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i < run.Length; i++)
        {
            char previous = run[i - 1], current = run[i];
            var boundary =
                (char.IsLower(previous) && char.IsUpper(current)) ||
                (char.IsUpper(previous) && char.IsUpper(current) && i + 1 < run.Length && char.IsLower(run[i + 1])) ||
                (char.IsLetter(previous) && char.IsDigit(current)) ||
                (char.IsDigit(previous) && char.IsLetter(current));
            if (boundary)
            {
                parts.Add(run[start..i]);
                start = i;
            }
        }

        parts.Add(run[start..]);
        return parts;
    }
}
