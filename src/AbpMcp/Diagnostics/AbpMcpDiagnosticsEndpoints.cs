using System.Text.Json;
using AbpMcp.Authorization;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using AbpMcp.Settings;
using AbpMcp.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Same browser-origin rule as the MCP endpoint (AbpMcpOptions.AllowedOrigins).
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AbpMcpOptions>>().Value;
        McpOriginGuard.Apply(discover, options);
        McpOriginGuard.Apply(explain, options);

        if (requireAuthorization)
        {
            discover.RequireAuthorization();
            explain.RequireAuthorization();
        }
    }

    /// <summary>
    /// Lists the tools the caller passes the authorizer for: the same gate <c>tools/list</c> applies, so the
    /// endpoint never reveals a tool's schema or required permissions to a caller who cannot call it.
    /// Administratively disabled tools stay listed (flagged) because explaining them is the point here.
    /// </summary>
    internal static async Task HandleDiscover(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<IDynamicMcpToolRegistry>();
        var authorizer = context.RequestServices.GetRequiredService<IAbpMcpToolAuthorizer>();
        // Same source as tools/list, so a disabled tool is flagged here exactly when it is hidden there.
        var disabledTools = await context.RequestServices.GetRequiredService<IDisabledToolsProvider>()
            .GetDisabledToolsAsync(context.RequestAborted).ConfigureAwait(false);
        registry.Initialize();

        var rows = new List<object>(registry.Tools.Count);
        foreach (var tool in registry.Tools)
        {
            if ((await authorizer.AuthorizeAsync(tool, context).ConfigureAwait(false)).Succeeded)
            {
                rows.Add(ToDiscoveryRow(tool, disabledTools.Contains(tool.Name)));
            }
        }

        var response = new
        {
            tool_count = rows.Count,
            tools = rows,
        };

        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    internal static async Task HandleExplain(HttpContext context)
    {
        var serviceFilter = context.Request.Query["service"].ToString();
        var options = context.RequestServices.GetRequiredService<IOptions<AbpMcpOptions>>().Value;
        var explanations = AbpMcpExposureExplainer.Explain(context.RequestServices, options, serviceFilter);

        var response = new
        {
            service_filter = string.IsNullOrEmpty(serviceFilter) ? null : serviceFilter,
            // The scope the reader applies; services outside it are never candidates, so they are not listed.
            exposed_assemblies = options.ExposedAssemblies.IsEmpty
                ? null
                : options.ExposedAssemblies.Select(e => e.Assembly.GetName().Name).ToArray(),
            // The subset abp-mcp found in the module graph rather than the host registering it.
            auto_discovered_assemblies = options.ExposedAssemblies
                .Where(e => e.IsAutoDiscovered)
                .Select(e => e.Assembly.GetName().Name)
                .ToArray(),
            count = explanations.Count,
            decisions = explanations,
        };

        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    private static object ToDiscoveryRow(ToolDescriptor tool, bool disabled) => new
    {
        name = tool.Name,
        disabled,
        description = tool.Description,
        kind = tool.Handler is not null ? "dynamic" : "service",
        service = tool.ServiceType?.FullName,
        method = tool.Method?.Name,
        http_method = tool.HttpMethod,
        parameters = tool.ParameterNames,
        required_permissions = tool.RequiredPermissions,
        requires_authenticated_user = tool.AuthorizeData.Count > 0,
        required_roles = tool.AuthorizeData.Select(a => a.Roles).Where(r => !string.IsNullOrEmpty(r)).ToArray(),
        authentication_schemes = tool.AuthorizeData.Select(a => a.AuthenticationSchemes).Where(s => !string.IsNullOrEmpty(s)).ToArray(),
        input_schema = tool.InputSchema,
        output_schema = McpOutputShape.ToProtocolSchema(tool.OutputSchema),
    };

    private static async Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync(json).ConfigureAwait(false);
    }
}
