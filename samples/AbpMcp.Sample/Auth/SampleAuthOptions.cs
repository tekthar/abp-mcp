namespace AbpMcp.Sample.Auth;

/// <summary>
/// <c>Sample:Auth</c> in configuration. With <see cref="Issuer"/> unset the sample stays anonymous; with it set,
/// <c>/mcp</c> requires a token from that issuer, set up the way <c>docs/oauth.md</c> describes. The bridge's
/// authenticated end-to-end tests (#112) run the sample this way against their stub authorization server.
/// </summary>
public sealed class SampleAuthOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Sample:Auth";

    /// <summary>The authorization server's issuer, exactly as its metadata states it.</summary>
    public string? Issuer { get; set; }

    /// <summary>The public URL of <c>/mcp</c>: the RFC 9728 <c>resource</c>.</summary>
    public string? Resource { get; set; }

    /// <summary>The audience MCP tokens carry.</summary>
    public string Audience { get; set; } = "LibraryMcp";

    /// <summary>The scope a token needs for <c>/mcp</c>.</summary>
    public string Scope { get; set; } = "LibraryMcp";

    /// <summary>
    /// Permission grants by user id (the token's <c>sub</c>): <c>userId:Permission,Permission;userId:Permission</c>,
    /// e.g. <c>Sample__Auth__Grants=3a1f…:Library.Members.Delete</c>. They live in the host's permission store, not
    /// in the token, as in a real ABP host. One string, so it fits an environment variable.
    /// </summary>
    public string? Grants { get; set; }

    /// <summary>The permissions <see cref="Grants"/> gives <paramref name="userId"/>.</summary>
    public IReadOnlySet<string> GrantsFor(string userId)
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in (Grants ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(entry[..separator].Trim(), userId, StringComparison.OrdinalIgnoreCase))
            {
                granted.UnionWith(entry[(separator + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return granted;
    }

    /// <summary>True when the sample runs authenticated.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer);
}
