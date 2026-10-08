using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AbpMcp.Addins.XmlDocumentation;

/// <summary>
/// Loads and queries .NET XML documentation files (the <c>&lt;summary&gt;</c>/<c>&lt;param&gt;</c> comments
/// emitted when <c>GenerateDocumentationFile</c> is on). Used by <see cref="XmlDocDescriptionAddin"/>
/// to turn the docs a service already carries into tool and parameter descriptions.
/// </summary>
/// <remarks>
/// Members are keyed by a normalized <c>Namespace.Type.Method</c> string rather than the exact XML
/// doc-comment member id. Reconstructing the exact id (generic arity, parameter type encoding, nested
/// type separators) from reflection is fiddly and error-prone; a type-plus-method key is unambiguous
/// here because abp-mcp never exposes overloaded methods (duplicate tool names throw at startup). The
/// lookup also tries the interface methods a service implements, so docs on the contract are found.
/// </remarks>
internal sealed class XmlDocumentationStore
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, MemberDoc> _members;

    private XmlDocumentationStore(IReadOnlyDictionary<string, MemberDoc> members) => _members = members;

    /// <summary>An empty store that resolves nothing.</summary>
    public static XmlDocumentationStore Empty { get; } =
        new(new Dictionary<string, MemberDoc>(StringComparer.Ordinal));

    /// <summary>
    /// Load and merge the XML doc files sitting next to the given assemblies' DLLs. Assemblies with no
    /// location (single-file/AOT) or no sibling <c>.xml</c> are skipped; an unreadable file is ignored.
    /// </summary>
    public static XmlDocumentationStore LoadForAssemblies(IEnumerable<Assembly> assemblies)
    {
        var members = new Dictionary<string, MemberDoc>(StringComparer.Ordinal);

        foreach (var assembly in assemblies.Where(a => a is not null).Distinct())
        {
            var xmlPath = TryGetXmlPath(assembly);
            if (xmlPath is null)
            {
                continue;
            }

            try
            {
                Merge(members, Parse(File.ReadAllText(xmlPath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // A missing or malformed doc file is not fatal — descriptions just stay mechanical.
            }
        }

        return new XmlDocumentationStore(members);
    }

    /// <summary>Build a store directly from XML content. Primarily for tests.</summary>
    public static XmlDocumentationStore ParseXml(string xml) => new(Parse(xml));

    /// <summary>
    /// Resolve the documentation for <paramref name="method"/>, trying the method itself and every
    /// interface method it implements. Returns null when nothing is documented.
    /// </summary>
    public MemberDoc? GetMethod(MethodInfo method)
    {
        foreach (var key in CandidateKeys(method))
        {
            if (_members.TryGetValue(key, out var doc))
            {
                return doc;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateKeys(MethodInfo method)
    {
        if (method.DeclaringType is not null)
        {
            yield return MethodKey(method.DeclaringType, method.Name);
        }

        var declaringType = method.DeclaringType;
        if (declaringType is null || declaringType.IsInterface)
        {
            yield break;
        }

        foreach (var iface in declaringType.GetInterfaces())
        {
            InterfaceMapping map;
            try
            {
                map = declaringType.GetInterfaceMap(iface);
            }
            catch (ArgumentException)
            {
                continue;
            }

            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i] == method)
                {
                    yield return MethodKey(iface, map.InterfaceMethods[i].Name);
                }
            }
        }
    }

    private static string MethodKey(Type type, string methodName) =>
        NormalizeTypeName(type.FullName ?? type.Name) + "." + methodName;

    private static string NormalizeTypeName(string fullName)
    {
        // Nested types report as Outer+Inner via reflection but Outer.Inner in XML ids; strip generic
        // arity markers (Foo`1) so both sides agree.
        var normalized = fullName.Replace('+', '.');
        var tick = normalized.IndexOf('`');
        while (tick >= 0)
        {
            var end = tick + 1;
            while (end < normalized.Length && char.IsDigit(normalized[end]))
            {
                end++;
            }

            normalized = normalized.Remove(tick, end - tick);
            tick = normalized.IndexOf('`');
        }

        return normalized;
    }

    private static Dictionary<string, MemberDoc> Parse(string xml)
    {
        var result = new Dictionary<string, MemberDoc>(StringComparer.Ordinal);
        var document = XDocument.Parse(xml);

        foreach (var member in document.Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            if (name is null || !name.StartsWith("M:", StringComparison.Ordinal))
            {
                continue;
            }

            var key = NormalizeMemberName(name);
            if (key is null)
            {
                continue;
            }

            var summary = Clean(member.Element("summary")?.Value);
            var parameters = member.Elements("param")
                .Where(p => !string.IsNullOrEmpty(p.Attribute("name")?.Value))
                .GroupBy(p => p.Attribute("name")!.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Clean(g.First().Value) ?? string.Empty, StringComparer.Ordinal);

            // First writer wins — abp-mcp does not expose overloads, so collisions are not expected.
            result.TryAdd(key, new MemberDoc(summary, parameters));
        }

        return result;
    }

    private static string? NormalizeMemberName(string memberName)
    {
        // "M:Namespace.Type.Method(System.Guid)" -> "Namespace.Type.Method" (normalized).
        var body = memberName[2..];
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body[..paren];
        }

        // Strip a generic-method arity marker on the method name (Method``1).
        var tick = body.IndexOf('`');
        if (tick >= 0)
        {
            body = body[..tick];
        }

        return body.Length == 0 ? null : NormalizeTypeName(body);
    }

    private static void Merge(Dictionary<string, MemberDoc> into, Dictionary<string, MemberDoc> from)
    {
        foreach (var (key, value) in from)
        {
            into.TryAdd(key, value);
        }
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WhitespaceRun.Replace(value, " ").Trim();
    }

    private static string? TryGetXmlPath(Assembly assembly)
    {
        string? location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        var xmlPath = Path.ChangeExtension(location, ".xml");
        return File.Exists(xmlPath) ? xmlPath : null;
    }
}

/// <summary>Documentation resolved for one member: its summary and per-parameter descriptions.</summary>
internal sealed record MemberDoc(string? Summary, IReadOnlyDictionary<string, string> Parameters);
