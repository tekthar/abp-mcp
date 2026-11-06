using System.Reflection;
using AbpMcp.Attributes;
using AbpMcp.Authorization;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Diagnostics;

/// <summary>
/// Builds the <c>/mcp/_explain</c> exposure decisions: for each app-service method ABP's api-definition
/// describes, whether abp-mcp exposes it as a tool and the attribute that decided it, and for an exposed one whether
/// the tool is listed or on demand and why (<see cref="ToolListingExplainer"/>). <c>[McpTool]</c> services
/// of the exposed assemblies that the api-definition does not describe get rows too, from
/// <see cref="UndescribedToolServiceExplainer"/>.
/// </summary>
internal static class AbpMcpExposureExplainer
{
    /// <summary>One row per service method in scope: whether it is exposed as a tool, and why.</summary>
    public static async Task<List<object>> ExplainAsync(IServiceProvider services, AbpMcpOptions options, string? serviceFilter)
    {
        var provider = services.GetRequiredService<IApiDescriptionModelProvider>();
        var model = await ApiDescriptionModels.CreateAsync(provider, new ApplicationApiDescriptionModelRequestDto
        {
            IncludeTypes = false,
        }).ConfigureAwait(false);

        // Same scope as ApiDefinitionReader: when assemblies are registered, nothing outside them is a
        // candidate tool, so listing those services would only disclose unrelated parts of the app.
        var allowedAssemblies = options.ExposedAssemblies.IsEmpty
            ? null
            : new HashSet<Assembly>(options.ExposedAssemblies.Select(e => e.Assembly));

        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();
        var toolsByMethod = ToolListingExplainer.IndexByMethod(registry.Tools);

        var rows = new List<object>();
        var describedServiceTypes = new HashSet<Type>();

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

                describedServiceTypes.Add(serviceType);
                if (!MatchesFilter(serviceType, serviceFilter))
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
                    rows.Add(ToolListingExplainer.DecisionRow(serviceType, method, included, reason, toolsByMethod));
                }
            }
        }

        // With no registered assemblies there is no bounded set to look for [McpTool] services in, and
        // scanning the whole AppDomain would disclose unrelated code; auto-discovery normally fills the set.
        if (allowedAssemblies is not null)
        {
            rows.AddRange(UndescribedToolServiceExplainer.Explain(model, allowedAssemblies, describedServiceTypes, serviceFilter));
        }

        return rows;
    }

    /// <summary>True when <paramref name="serviceType"/> matches the optional <c>?service=</c> filter.</summary>
    internal static bool MatchesFilter(Type serviceType, string? serviceFilter) =>
        string.IsNullOrEmpty(serviceFilter) ||
        serviceType.FullName!.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase) ||
        serviceType.Name.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every tool that requires a permission or policy this host does not define, with that name. The authorizer
    /// refuses such a tool to every caller, so it never appears in <c>tools/list</c>; this says why.
    /// </summary>
    public static async Task<List<object>> FindUndefinedPermissionsAsync(IServiceProvider services)
    {
        var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
        var policies = services.GetRequiredService<IAuthorizationPolicyProvider>();
        registry.Initialize();

        var rows = new List<object>();
        foreach (var tool in registry.Tools)
        {
            var undefined = await AbpMcpToolAuthorizer.FindUndefinedPolicyNamesAsync(policies, tool).ConfigureAwait(false);
            rows.AddRange(undefined.Select(name => new { tool = tool.Name, permission = name }));
        }

        return rows;
    }

    /// <summary>The attribute decision for one method of a service: exposed or not, and which attribute decided it.</summary>
    internal static (bool Included, string Reason) Decide(
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
