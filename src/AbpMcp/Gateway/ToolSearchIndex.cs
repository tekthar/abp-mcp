using System.Runtime.CompilerServices;
using AbpMcp.Metadata;

namespace AbpMcp.Gateway;

/// <summary>
/// Keyword search over a tool set for <c>mcp_search_tools</c>. Each tool is indexed in three weighted fields (its name,
/// then its service and namespace group, then its description). A query token counts once, at its best field; an exact
/// token scores fully and a prefix match partly, and a tool must match a minimum share of the query's tokens: all of
/// them for a one- or two-token query, 60% (rounded up) for a longer one. An exact or leading name match earns a bonus.
/// </summary>
/// <remarks>
/// Ranking only: the index knows nothing about the caller. <c>mcp_search_tools</c> filters the matches through the same
/// visibility gate as <c>tools/list</c>, so a tool the caller cannot use is never returned.
/// </remarks>
internal sealed class ToolSearchIndex
{
    internal const double NameWeight = 3.0;
    internal const double ServiceWeight = 2.0;
    internal const double DescriptionWeight = 1.0;
    internal const double PrefixQuality = 0.5;
    internal const double ExactNameBonus = 10.0;
    internal const double NamePrefixBonus = 3.0;
    internal const double LongQueryCoverage = 0.6;

    private static readonly string[] ServiceSuffixes = ["AppService", "ApplicationService", "Service"];

    // Keyed by the registry's tool list, which Refresh replaces rather than mutates, so a rebuilt tool set gets a new index.
    private static readonly ConditionalWeakTable<IReadOnlyList<ToolDescriptor>, ToolSearchIndex> Cache = new();

    private readonly Entry[] _entries;

    public ToolSearchIndex(IEnumerable<ToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _entries = tools.Select(tool => new Entry(
            tool,
            ToolSearchTokenizer.FieldTokens(tool.Name),
            ToolSearchTokenizer.FieldTokens(ServiceText(tool)),
            ToolSearchTokenizer.FieldTokens(tool.Description),
            ToolSearchTokenizer.Compact(tool.Name))).ToArray();
    }

    /// <summary>The index of <paramref name="tools"/>, built once per tool-set instance.</summary>
    public static ToolSearchIndex For(IReadOnlyList<ToolDescriptor> tools) => Cache.GetValue(tools, t => new ToolSearchIndex(t));

    /// <summary>How many of <paramref name="queryTokenCount"/> tokens a tool must match to be returned.</summary>
    public static int RequiredMatches(int queryTokenCount) =>
        queryTokenCount <= 2 ? queryTokenCount : (int)Math.Ceiling(queryTokenCount * LongQueryCoverage);

    /// <summary>Every tool that meets the coverage rule for <paramref name="query"/>, best first (ties by name).</summary>
    public IReadOnlyList<ToolSearchMatch> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = ToolSearchTokenizer.QueryTokens(query);
        if (tokens.Count == 0)
        {
            return Array.Empty<ToolSearchMatch>();
        }

        var required = RequiredMatches(tokens.Count);
        var compactQuery = ToolSearchTokenizer.Compact(query);
        var matches = new List<ToolSearchMatch>();
        foreach (var entry in _entries)
        {
            var (matched, score) = ScoreTokens(entry, tokens);
            if (matched < required)
            {
                continue;
            }

            matches.Add(new ToolSearchMatch(entry.Tool, score + NameBonus(entry.CompactName, compactQuery)));
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static (int Matched, double Score) ScoreTokens(Entry entry, IReadOnlyList<string> tokens)
    {
        var matched = 0;
        var score = 0.0;
        foreach (var token in tokens)
        {
            var best = Math.Max(
                NameWeight * Quality(entry.Name, token),
                Math.Max(ServiceWeight * Quality(entry.Service, token), DescriptionWeight * Quality(entry.Description, token)));
            if (best > 0)
            {
                matched++;
                score += best;
            }
        }

        return (matched, score);
    }

    /// <summary>1 for an exact token, <see cref="PrefixQuality"/> when one is a prefix of the other (3+ and 4+ characters), else 0.</summary>
    private static double Quality(HashSet<string> field, string token)
    {
        if (field.Contains(token))
        {
            return 1.0;
        }

        foreach (var candidate in field)
        {
            if ((token.Length >= 3 && candidate.StartsWith(token, StringComparison.Ordinal)) ||
                (candidate.Length >= 4 && token.StartsWith(candidate, StringComparison.Ordinal)))
            {
                return PrefixQuality;
            }
        }

        return 0.0;
    }

    private static double NameBonus(string compactName, string compactQuery)
    {
        if (compactQuery.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(compactName, compactQuery, StringComparison.Ordinal))
        {
            return ExactNameBonus;
        }

        return compactQuery.Length >= 3 && compactName.StartsWith(compactQuery, StringComparison.Ordinal) ? NamePrefixBonus : 0.0;
    }

    /// <summary>The service's short name and its namespace's last segment (the group), e.g. <c>Loan Library</c>.</summary>
    private static string? ServiceText(ToolDescriptor tool)
    {
        if (tool.ServiceType is null)
        {
            return null;
        }

        var name = tool.ServiceType.Name;
        var suffix = ServiceSuffixes.FirstOrDefault(s => name.EndsWith(s, StringComparison.Ordinal) && name.Length > s.Length);
        var shortName = suffix is null ? name : name[..^suffix.Length];
        var group = tool.ServiceType.Namespace?.Split('.').LastOrDefault();
        return group is null ? shortName : $"{shortName} {group}";
    }

    private sealed record Entry(
        ToolDescriptor Tool,
        HashSet<string> Name,
        HashSet<string> Service,
        HashSet<string> Description,
        string CompactName);
}

/// <summary>A tool <see cref="ToolSearchIndex.Search"/> returned, with its relevance score.</summary>
/// <param name="Tool">The matching tool.</param>
/// <param name="Score">Higher is better; comparable only within one search.</param>
internal readonly record struct ToolSearchMatch(ToolDescriptor Tool, double Score);
