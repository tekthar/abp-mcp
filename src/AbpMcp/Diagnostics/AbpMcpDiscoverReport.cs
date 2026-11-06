using AbpMcp.Attributes;
using AbpMcp.Authorization;
using AbpMcp.Gateway;
using AbpMcp.Listing;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using AbpMcp.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.Diagnostics;

/// <summary>
/// The <c>/mcp/_discover</c> payload: every tool the caller passes the authorizer for, with its schemas, required
/// permissions, kill-switch flag, listing and approximate context cost, plus the gateway tools when they are on.
/// </summary>
internal static class AbpMcpDiscoverReport
{
    /// <summary>
    /// Lists the tools the caller passes the authorizer for: the same gate <c>tools/list</c> applies, so the report
    /// never reveals a tool's schema or required permissions to a caller who cannot call it. Administratively
    /// disabled tools stay listed (flagged) because explaining them is the point here.
    /// </summary>
    public static async Task<object> BuildAsync(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<IDynamicMcpToolRegistry>();
        var authorizer = context.RequestServices.GetRequiredService<IAbpMcpToolAuthorizer>();
        // Same source as tools/list, so a disabled tool is flagged here exactly when it is hidden there.
        var disabledTools = await context.RequestServices.GetRequiredService<IDisabledToolsProvider>()
            .GetDisabledToolsAsync(context.RequestAborted).ConfigureAwait(false);
        registry.Initialize();

        var rows = new List<object>(registry.Tools.Count);
        int listedTokens = 0, onDemandTokens = 0;
        foreach (var tool in registry.Tools)
        {
            if (!(await authorizer.AuthorizeAsync(tool, context).ConfigureAwait(false)).Succeeded)
            {
                continue;
            }

            var disabled = disabledTools.Contains(tool.Name);
            var tokens = SchemaTokens(tool);
            rows.Add(ToRow(tool, disabled, tokens));
            if (disabled)
            {
                continue;
            }

            if (tool.Listing == McpToolListing.OnDemand)
            {
                onDemandTokens += tokens;
            }
            else
            {
                listedTokens += tokens;
            }
        }

        var gateway = GatewayToolDefinitions.IsEnabled(registry.Tools)
            ? GatewayToolDefinitions.Tools.Where(t => !disabledTools.Contains(t.Name)).ToList()
            : new List<ToolDescriptor>();

        return new
        {
            tool_count = rows.Count,
            // Advertised in tools/list whenever at least one tool is on demand.
            gateway_tools = gateway.Select(t => t.Name).ToArray(),
            // What the caller's tools/list costs (listed + gateway) and what on-demand tools keep out of it.
            schema_tokens = new
            {
                listed = listedTokens,
                gateway = gateway.Sum(SchemaTokens),
                on_demand = onDemandTokens,
            },
            tools = rows,
        };
    }

    /// <summary>
    /// A rough count of the context tokens a tool costs in <c>tools/list</c>: the characters of its description and its
    /// input and output schemas (as advertised), divided by four. For comparing tools, not for billing.
    /// </summary>
    internal static int SchemaTokens(ToolDescriptor tool)
    {
        var characters = tool.Description.Length
            + tool.InputSchema.ToJsonString().Length
            + (McpOutputShape.ToProtocolSchema(tool.OutputSchema)?.ToJsonString().Length ?? 0);
        return (characters + 3) / 4;
    }

    private static object ToRow(ToolDescriptor tool, bool disabled, int schemaTokens) => new
    {
        name = tool.Name,
        disabled,
        listing = ToolListingResolver.WireName(tool.Listing),
        schema_tokens = schemaTokens,
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
}
