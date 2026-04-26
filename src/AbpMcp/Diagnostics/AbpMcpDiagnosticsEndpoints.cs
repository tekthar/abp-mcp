using System.Reflection;
using System.Text.Json;
using AbpMcp.Attributes;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Diagnostics;

/// <summary>
/// Design-time HTTP endpoints exposed alongside the MCP transport for debugging.
/// Mounted at <c>{path}/_discover</c> and <c>{path}/_explain</c> by <c>MapAbpMcp</c>.
/// </summary>
/// <remarks>
/// The single most painful failure mode of any auto-generator is "I added the attribute,
/// why isn't my tool showing up?" Without these endpoints every misconfiguration is a
/// debugger session. With them it is a curl.
/// </remarks>
public static class AbpMcpDiagnosticsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Mount the diagnostics endpoints under the given base path. Called by
    /// <see cref="AbpMcpBuilderExtensions.MapAbpMcp"/>; not intended to be called directly.
    /// </summary>
    /// <param name="endpoints">The route builder to mount on.</param>
    /// <param name="basePath">The base path (matches <see cref="AbpMcpOptions.Path"/>).</param>
    /// <param name="requireAuthorization">
    /// When true, both endpoints get <c>RequireAuthorization()</c>. The exposure decisions and
    /// parameter schemas they reveal are the same application structure that gates authentication
    /// on the MCP endpoint itself; they must follow the same auth posture.
    /// </param>
    public static void Map(IEndpointRouteBuilder endpoints, string basePath, bool requireAuthorization)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(basePath);

        var discover = endpoints.MapGet(basePath + "/_discover", HandleDiscover);
        var explain = endpoints.MapGet(basePath + "/_explain", HandleExplain);

        if (requireAuthorization)
        {
            discover.RequireAuthorization();
            explain.RequireAuthorization();
        }
    }

    private static async Task HandleDiscover(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        var response = new
        {
            tool_count = registry.Tools.Count,
            tools = registry.Tools.Select(ToDiscoveryRow).ToArray(),
        };

        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    private static async Task HandleExplain(HttpContext context)
    {
        var serviceFilter = context.Request.Query["service"].ToString();
        var explanations = BuildExplanations(context.RequestServices, serviceFilter);

        var response = new
        {
            service_filter = string.IsNullOrEmpty(serviceFilter) ? null : serviceFilter,
            count = explanations.Count,
            decisions = explanations,
        };

        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    private static object ToDiscoveryRow(ToolDescriptor tool) => new
    {
        name = tool.Name,
        description = tool.Description,
        service = tool.ServiceType.FullName,
        method = tool.Method.Name,
        parameters = tool.ParameterNames,
        required_permissions = tool.RequiredPermissions,
        input_schema = tool.InputSchema,
    };

    private static List<object> BuildExplanations(IServiceProvider services, string? serviceFilter)
    {
        var provider = services.GetRequiredService<IApiDescriptionModelProvider>();
        var model = provider.CreateApiModel(new ApplicationApiDescriptionModelRequestDto
        {
            IncludeTypes = false,
        });

        var rows = new List<object>();

        // Compiler-generated types (<PrivateImplementationDetails>, anonymous types, closure
        // types) collide on FullName across assemblies. Group and take the first match —
        // any of them is fine because we only resolve by FullName for ABP-declared services.
        var loadedTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => !string.IsNullOrEmpty(t.FullName))
            .GroupBy(t => t.FullName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var module in model.Modules.Values)
        {
            foreach (var controller in module.Controllers.Values)
            {
                if (!loadedTypes.TryGetValue(controller.Type, out var serviceType))
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

        var methodIgnore = method.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);
        if (methodIgnore is not null)
        {
            return (false, $"Excluded: [McpIgnore] on {serviceType.Name}.{method.Name}.");
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

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync(json).ConfigureAwait(false);
    }
}
