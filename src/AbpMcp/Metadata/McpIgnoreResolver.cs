using System.Reflection;
using System.Runtime.CompilerServices;
using AbpMcp.Attributes;

namespace AbpMcp.Metadata;

/// <summary>
/// Single source of truth for "is this method opted out with <see cref="McpIgnoreAttribute"/>?",
/// shared by <see cref="ApiDefinitionReader"/> (what is exposed) and the <c>_explain</c> endpoint
/// (what it reports), so the two can never disagree.
/// </summary>
/// <remarks>
/// <c>GetCustomAttribute(inherit: true)</c> walks base-class overrides only, never the interface
/// members a method implements. ABP developers commonly annotate the <c>IXxxAppService</c> contract,
/// and <see cref="McpIgnoreAttribute"/>'s usage permits methods there, so the interface map is
/// consulted too. <c>[McpIgnore]</c> always wins, wherever it is placed.
/// </remarks>
internal static class McpIgnoreResolver
{
    /// <summary>
    /// The member carrying <see cref="McpIgnoreAttribute"/> for <paramref name="method"/> — the method
    /// itself, or an interface method it implements on <paramref name="serviceType"/>. Null when not ignored.
    /// </summary>
    public static MethodInfo? FindIgnoringMember(Type serviceType, MethodInfo method)
    {
        if (method.GetCustomAttribute<McpIgnoreAttribute>(inherit: true) is not null)
        {
            return method;
        }

        var ignored = IgnoredByInterface.GetValue(serviceType, BuildIgnoredByInterface);
        return ignored.Count > 0 && ignored.TryGetValue(KeyOf(method), out var interfaceMethod)
            ? interfaceMethod
            : null;
    }

    // Built once per service type: implementing method -> the interface method carrying [McpIgnore].
    // Attributes are fixed at compile time, so the result never goes stale. ConditionalWeakTable keeps
    // collectible (plugin) types unloadable and is safe for concurrent first use.
    private static readonly ConditionalWeakTable<Type, Dictionary<MethodKey, MethodInfo>> IgnoredByInterface = new();

    private static Dictionary<MethodKey, MethodInfo> BuildIgnoredByInterface(Type serviceType)
    {
        var ignored = new Dictionary<MethodKey, MethodInfo>();
        foreach (var iface in serviceType.GetInterfaces())
        {
            // Most interfaces on an ABP service are method-less markers or carry no [McpIgnore];
            // skip building their interface map entirely.
            if (!iface.GetMethods().Any(m => m.IsDefined(typeof(McpIgnoreAttribute), inherit: false)))
            {
                continue;
            }

            InterfaceMapping map;
            try
            {
                map = serviceType.GetInterfaceMap(iface);
            }
            catch (ArgumentException)
            {
                continue;
            }

            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].IsDefined(typeof(McpIgnoreAttribute), inherit: false))
                {
                    ignored.TryAdd(KeyOf(map.TargetMethods[i]), map.InterfaceMethods[i]);
                }
            }
        }

        return ignored;
    }

    // Key by identity rather than MethodInfo equality, which also compares ReflectedType and so
    // can miss an inherited implementation obtained through a derived type.
    private static MethodKey KeyOf(MethodInfo method) => new(method.Module, method.MetadataToken, method.DeclaringType);

    private readonly record struct MethodKey(Module Module, int MetadataToken, Type? DeclaringType);
}
