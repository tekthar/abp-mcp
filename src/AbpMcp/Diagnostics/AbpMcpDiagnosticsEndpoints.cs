using System.Text.Json;
using AbpMcp.Registration;
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
    public static void Map(IEndpointRouteBuilder endpoints, string basePath, bool requireAuthorization) =>
        Map(endpoints, basePath, requireAuthorization, authorizationPolicy: null);

    /// <summary>
    /// Mount the diagnostics endpoints under the given base path, guarded by a named authorization policy.
    /// Called by <see cref="AbpMcpBuilderExtensions.MapAbpMcp"/>; not intended to be called directly.
    /// </summary>
    /// <param name="endpoints">The route builder to mount on.</param>
    /// <param name="basePath">The base path (matches <see cref="AbpMcpOptions.Path"/>).</param>
    /// <param name="requireAuthorization">When true, both endpoints require authorization.</param>
    /// <param name="authorizationPolicy">
    /// The policy both endpoints require (<see cref="AbpMcpOptions.AuthorizationPolicy"/>), so they are never
    /// looser than the MCP endpoint. <c>null</c> or blank means the host's default policy. Ignored when
    /// <paramref name="requireAuthorization"/> is false.
    /// </param>
    public static void Map(IEndpointRouteBuilder endpoints, string basePath, bool requireAuthorization, string? authorizationPolicy)
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
            RequireAuthorization(discover, authorizationPolicy);
            RequireAuthorization(explain, authorizationPolicy);
        }
    }

    /// <summary>
    /// Require <paramref name="policy"/> on <paramref name="builder"/>, or the host's default policy when it is
    /// null or blank. Shared with <c>MapAbpMcp</c> so the MCP endpoint and the diagnostics endpoints cannot end
    /// up on different policies.
    /// </summary>
    internal static void RequireAuthorization(IEndpointConventionBuilder builder, string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            builder.RequireAuthorization();
        }
        else
        {
            builder.RequireAuthorization(policy);
        }
    }

    /// <summary>
    /// Lists the tools the caller passes the authorizer for (the <c>tools/list</c> gate), with their listing and
    /// approximate schema cost; see <see cref="AbpMcpDiscoverReport"/>.
    /// </summary>
    internal static async Task HandleDiscover(HttpContext context) =>
        await WriteJsonAsync(context, await AbpMcpDiscoverReport.BuildAsync(context).ConfigureAwait(false)).ConfigureAwait(false);

    internal static async Task HandleExplain(HttpContext context)
    {
        var serviceFilter = context.Request.Query["service"].ToString();
        var options = context.RequestServices.GetRequiredService<IOptions<AbpMcpOptions>>().Value;
        var explanations = await AbpMcpExposureExplainer.ExplainAsync(context.RequestServices, options, serviceFilter)
            .ConfigureAwait(false);
        var undefinedPermissions = await AbpMcpExposureExplainer.FindUndefinedPermissionsAsync(context.RequestServices)
            .ConfigureAwait(false);

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
            // Tools no caller can use: they require a permission or policy this host does not define.
            undefined_permissions = undefinedPermissions,
            // The host's listing patterns (AbpMcpOptions.ToolListing); each decision row names the one that applied.
            tool_listing = new
            {
                listed = options.ToolListing.ListedPatterns,
                on_demand = options.ToolListing.OnDemandPatterns,
            },
            count = explanations.Count,
            decisions = explanations,
            // Tools add-ins contribute have no service method, so no decision row; their listing is explained here.
            dynamic_tools = string.IsNullOrEmpty(serviceFilter)
                ? ToolListingExplainer.DynamicToolRows(context.RequestServices.GetRequiredService<IDynamicMcpToolRegistry>().Tools)
                : new List<object>(),
        };

        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync(json).ConfigureAwait(false);
    }
}
