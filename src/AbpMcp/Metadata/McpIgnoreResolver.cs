using System.Reflection;
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

        foreach (var iface in serviceType.GetInterfaces())
        {
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
                if (IsSameMethod(map.TargetMethods[i], method) &&
                    map.InterfaceMethods[i].GetCustomAttribute<McpIgnoreAttribute>() is not null)
                {
                    return map.InterfaceMethods[i];
                }
            }
        }

        return null;
    }

    // Compare by identity rather than MethodInfo equality, which also compares ReflectedType and so
    // can miss an inherited implementation obtained through a derived type.
    private static bool IsSameMethod(MethodInfo left, MethodInfo right) =>
        left.MetadataToken == right.MetadataToken &&
        left.Module == right.Module &&
        left.DeclaringType == right.DeclaringType;
}
