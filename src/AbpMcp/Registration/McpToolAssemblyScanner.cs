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
    private static readonly string AbpMcpAssemblyName = typeof(McpToolAttribute).Assembly.GetName().Name!;

    /// <summary>The distinct assemblies in <paramref name="candidates"/> that contain an <c>[McpTool]</c> service.</summary>
    public static IReadOnlyList<Assembly> FindToolAssemblies(IEnumerable<Assembly> candidates) =>
        candidates.Distinct().Where(ContainsMcpTools).ToList();

    /// <summary>
    /// True when <paramref name="assembly"/> declares an <c>[McpTool]</c> service. Only assemblies that
    /// reference abp-mcp directly are scanned, which skips the framework assemblies every ABP host loads.
    /// </summary>
    public static bool ContainsMcpTools(Assembly assembly)
    {
        if (!ReferencesAbpMcp(assembly))
        {
            return false;
        }

        return LoadableTypes(assembly).Any(IsMcpToolService);
    }

    private static bool ReferencesAbpMcp(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Any(r => r.Name == AbpMcpAssemblyName);

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
