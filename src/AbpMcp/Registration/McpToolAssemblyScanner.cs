using System.Reflection;
using AbpMcp.Attributes;

namespace AbpMcp.Registration;

/// <summary>
/// Finds the assemblies that declare <c>[McpTool]</c> services, using the same attribute semantics as
/// <see cref="Metadata.ApiDefinitionReader"/>: <c>[McpTool]</c> on a concrete class or one of its public
/// instance methods, inherited attributes included.
/// </summary>
internal static class McpToolAssemblyScanner
{
    /// <summary>
    /// A tool assembly references one of these: AbpMcp.Abstractions when it tags services through the
    /// dependency-free package (the recommended shape for *.Application, #98), AbpMcp when it references the
    /// full package (and, for binaries built against 0.4.x, sees the attributes there via type forwarding).
    /// </summary>
    private static readonly HashSet<string> AbpMcpAssemblyNames = new(StringComparer.Ordinal)
    {
        typeof(McpToolAttribute).Assembly.GetName().Name!,
        typeof(McpToolAssemblyScanner).Assembly.GetName().Name!,
    };

    /// <summary>The distinct assemblies in <paramref name="candidates"/> that contain an <c>[McpTool]</c> service.</summary>
    public static IReadOnlyList<Assembly> FindToolAssemblies(IEnumerable<Assembly> candidates) =>
        candidates.Distinct().Where(ContainsMcpTools).ToList();

    /// <summary>
    /// True when <paramref name="assembly"/> declares an <c>[McpTool]</c> service. Only assemblies that
    /// reference abp-mcp directly (AbpMcp or AbpMcp.Abstractions) are scanned, which skips the framework assemblies every ABP host loads.
    /// </summary>
    public static bool ContainsMcpTools(Assembly assembly)
    {
        if (!ReferencesAbpMcp(assembly))
        {
            return false;
        }

        return LoadableTypes(assembly).Any(IsMcpToolService);
    }

    /// <summary>
    /// Every <c>[McpTool]</c> service type declared in <paramref name="assemblies"/>, whether or not ABP's
    /// api-definition describes it. <c>/mcp/_explain</c> uses this to report the ones it does not (#101).
    /// </summary>
    public static IEnumerable<Type> FindToolServiceTypes(IEnumerable<Assembly> assemblies) =>
        assemblies.Distinct().Where(ReferencesAbpMcp).SelectMany(LoadableTypes).Where(IsMcpToolService);

    private static bool ReferencesAbpMcp(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Any(r => r.Name is not null && AbpMcpAssemblyNames.Contains(r.Name));

    internal static bool IsMcpToolService(Type type)
    {
        if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
        {
            return false;
        }

        return type.IsDefined(typeof(McpToolAttribute), inherit: true) ||
               type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                   .Any(m => m.IsDefined(typeof(McpToolAttribute), inherit: true));
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A type whose dependency is missing cannot be an app service the host can run anyway.
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
