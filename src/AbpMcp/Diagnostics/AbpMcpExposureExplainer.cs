using System.Reflection;
using AbpMcp.Attributes;
using AbpMcp.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Diagnostics;

/// <summary>
/// Builds the <c>/mcp/_explain</c> exposure decisions: for each app-service method ABP's api-definition
/// describes, whether abp-mcp exposes it as a tool and the attribute that decided it.
/// </summary>
internal static class AbpMcpExposureExplainer
{
    /// <summary>One row per service method in scope: whether it is exposed as a tool, and why.</summary>
    public static List<object> Explain(IServiceProvider services, AbpMcpOptions options, string? serviceFilter)
    {
        var provider = services.GetRequiredService<IApiDescriptionModelProvider>();
        var model = provider.CreateApiModel(new ApplicationApiDescriptionModelRequestDto
        {
            IncludeTypes = false,
        });

        // Same scope as ApiDefinitionReader: when assemblies are registered, nothing outside them is a
        // candidate tool, so listing those services would only disclose unrelated parts of the app.
        var allowedAssemblies = options.ExposedAssemblies.IsEmpty
            ? null
            : new HashSet<Assembly>(options.ExposedAssemblies.Select(e => e.Assembly));

        var rows = new List<object>();

        foreach (var module in model.Modules.Values)
        {
            foreach (var controller in module.Controllers.Values)
            {
                var serviceType = ResolveType(controller.Type);
                if (serviceType is null ||
                    (allowedAssemblies is not null && !allowedAssemblies.Contains(serviceType.Assembly)))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(serviceFilter) &&
                    !serviceType.FullName!.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase) &&
                    !serviceType.Name.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var classExpose = serviceType.GetCustomAttribute<McpToolAttribute>(inherit: true);
                var classIgnore = serviceType.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);

                foreach (var action in controller.Actions.Values)
                {
                    var method = ResolveMethod(serviceType, action);
                    if (method is null)
                    {
                        rows.Add(new
                        {
                            service = serviceType.FullName,
                            method = action.Name,
                            included = false,
                            reason = "Method not found on service type (likely interface mismatch).",
                        });
                        continue;
                    }

                    var (included, reason) = Decide(serviceType, method, classExpose, classIgnore);
                    rows.Add(new
                    {
                        service = serviceType.FullName,
                        method = method.Name,
                        included,
                        reason,
                    });
                }
            }
        }

        return rows;
    }

    private static (bool Included, string Reason) Decide(
        Type serviceType,
        MethodInfo method,
        McpToolAttribute? classExpose,
        McpIgnoreAttribute? classIgnore)
    {
        if (classIgnore is not null)
        {
            return (false, $"Excluded: [McpIgnore] on {serviceType.Name}.");
        }

        var ignoringMember = McpIgnoreResolver.FindIgnoringMember(serviceType, method);
        if (ignoringMember is not null)
        {
            var owner = ignoringMember.DeclaringType is { IsInterface: true } iface ? iface.Name : serviceType.Name;
            return (false, $"Excluded: [McpIgnore] on {owner}.{method.Name}.");
        }

        var methodExpose = method.GetCustomAttribute<McpToolAttribute>(inherit: true);
        if (methodExpose is not null)
        {
            return (true, $"Included: [McpTool] on method {method.Name}.");
        }

        if (classExpose is not null)
        {
            return (true, $"Included: [McpTool] on class {serviceType.Name}.");
        }

        return (false, "Excluded: opt-in default and no [McpTool] attribute on class or method.");
    }

    private static MethodInfo? ResolveMethod(Type serviceType, ActionApiDescriptionModel action)
    {
        var candidates = serviceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, action.Name, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        return candidates.FirstOrDefault(m => m.GetParameters().Length == action.Parameters.Count);
    }

    /// <summary>
    /// Resolve a controller's service type by name, as ApiDefinitionReader does, instead of indexing every
    /// type in the AppDomain on each request.
    /// </summary>
    private static Type? ResolveType(string typeName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);
}
