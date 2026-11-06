using System.Reflection;
using AbpMcp.Attributes;
using AbpMcp.Registration;
using Volo.Abp;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Diagnostics;

/// <summary>
/// Explains the <c>[McpTool]</c> services of the exposed assemblies that ABP's api-definition does not describe,
/// and that therefore cannot become tools (#101). The common case is ABP's pattern for an endpoint that needs MVC:
/// the app service is <c>[RemoteService(IsEnabled = false)]</c> and a hand-written controller serves it, so the
/// api-definition lists the controller instead. Without these rows such a service would be missing from
/// <c>/mcp/_explain</c> altogether, which is exactly the case a developer opens it for.
/// </summary>
internal static class UndescribedToolServiceExplainer
{
    /// <summary>Where the docs explain the limitation and the wrapper pattern.</summary>
    internal const string DocsPath = "docs/hand-written-controllers.md";

    /// <summary>
    /// One row per candidate method of each <c>[McpTool]</c> service in <paramref name="exposedAssemblies"/>
    /// whose type is not in <paramref name="describedServiceTypes"/>.
    /// </summary>
    public static IEnumerable<object> Explain(
        ApplicationApiDescriptionModel model,
        IEnumerable<Assembly> exposedAssemblies,
        IReadOnlySet<Type> describedServiceTypes,
        string? serviceFilter)
    {
        var serviceTypes = McpToolAssemblyScanner.FindToolServiceTypes(exposedAssemblies)
            .Where(t => !describedServiceTypes.Contains(t))
            .Where(t => AbpMcpExposureExplainer.MatchesFilter(t, serviceFilter))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var serviceType in serviceTypes)
        {
            var notDescribed = Reason(serviceType, FindServingController(model, serviceType));
            var classExpose = serviceType.GetCustomAttribute<McpToolAttribute>(inherit: true);
            var classIgnore = serviceType.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);

            foreach (var method in CandidateMethods(serviceType))
            {
                // A method abp-mcp would skip anyway keeps that reason; only would-be tools get the new one.
                var (included, reason) = AbpMcpExposureExplainer.Decide(serviceType, method, classExpose, classIgnore);
                yield return new
                {
                    service = serviceType.FullName,
                    method = method.Name,
                    included = false,
                    reason = included ? notDescribed : reason,
                };
            }
        }
    }

    private static string Reason(Type serviceType, string? controller)
    {
        var notRemoted = serviceType.GetCustomAttribute<RemoteServiceAttribute>(inherit: true) is { IsEnabled: false };
        var servedBy = controller is null ? string.Empty : $" It is served by the hand-written controller {controller}.";

        if (notRemoted)
        {
            return $"Excluded: {serviceType.Name} is not in ABP's api-definition: it is [RemoteService(IsEnabled = false)], " +
                   $"so it is served by a hand-written controller or not remoted at all.{servedBy} abp-mcp only exposes app " +
                   "services the api-definition describes, and hand-written controllers are not supported. Wrap the " +
                   "JSON-friendly operations in a thin, remoted [McpTool] app service that delegates to this one. " +
                   $"See {DocsPath}.";
        }

        return $"Excluded: {serviceType.Name} is not in ABP's api-definition.{servedBy} Only app services ABP remotes " +
               "become tools: make sure it implements IApplicationService (or IRemoteService), is not " +
               "[RemoteService(IsEnabled = false)], and is registered with ABP's dependency injection. " +
               $"See {DocsPath}.";
    }

    /// <summary>
    /// The api-definition's controller that implements one of <paramref name="serviceType"/>'s interfaces, which
    /// for ABP's pattern is the hand-written controller standing in for the service.
    /// </summary>
    private static string? FindServingController(ApplicationApiDescriptionModel model, Type serviceType)
    {
        var interfaces = serviceType.GetInterfaces()
            .Select(i => i.FullName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        return model.Modules.Values
            .SelectMany(m => m.Controllers.Values)
            .Where(c => c.Interfaces.Any(i => interfaces.Contains(i.Type)))
            .Select(c => c.Type)
            .FirstOrDefault();
    }

    /// <summary>
    /// The methods that would be tools if the service were described: the public instance methods the service's
    /// own assembly declares, plus any inherited method tagged <c>[McpTool]</c>.
    /// </summary>
    private static IEnumerable<MethodInfo> CandidateMethods(Type serviceType) =>
        serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType is not null && m.DeclaringType != typeof(object))
            .Where(m => m.DeclaringType!.Assembly == serviceType.Assembly ||
                        m.IsDefined(typeof(McpToolAttribute), inherit: true))
            .OrderBy(m => m.Name, StringComparer.Ordinal);
}
