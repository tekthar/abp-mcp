namespace AbpMcp.Listing;

/// <summary>
/// The glob rule of <see cref="ToolListingOptions"/>: <c>*</c> matches any run of characters (including none),
/// <c>?</c> exactly one, anything else itself, compared ordinally against the whole tool name.
/// </summary>
internal static class ToolNameGlob
{
    /// <summary>True when <paramref name="name"/> matches <paramref name="pattern"/> as a whole.</summary>
    public static bool IsMatch(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);

        // Iterative wildcard match that backtracks to the last '*' only: no regex, no allocation.
        int p = 0, n = 0, star = -1, starName = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starName = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++starName;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>The first of <paramref name="patterns"/> that <paramref name="name"/> matches, or null.</summary>
    public static string? FirstMatch(IEnumerable<string> patterns, string name) =>
        patterns.FirstOrDefault(pattern => IsMatch(pattern, name));
}
