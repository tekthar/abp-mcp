using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Metadata;

/// <summary>
/// Default implementation of <see cref="IToolDescriptorBuilder"/>.
/// Produces tool names of the form <c>{ServiceShortName}_{MethodShortName}</c>,
/// pulls descriptions from XML docs or the <c>[McpTool(Description = ...)]</c> override,
/// and emits JSON Schema for the input object by translating the method's parameters via
/// <see cref="JsonSchemaMapper"/>.
/// </summary>
internal sealed class ToolDescriptorBuilder : IToolDescriptorBuilder
{
    public ToolDescriptor Build(
        Type serviceType,
        MethodInfo method,
        ActionApiDescriptionModel action,
        McpToolAttribute expose,
        AbpMcpOptions options)
    {
        var name = expose.Name ?? options.ToolNameNormalizer(new ToolNamingContext
        {
            ServiceType = serviceType,
            Method = method,
            ConfiguredPrefix = options.ToolNamePrefix,
        });
        var description = expose.Description ?? BuildDescription(method, action);
        var (schema, paramNames) = BuildInputSchema(method);
        var (permissions, authorizeData) = ResolveAuthorization(serviceType, method);

        return new ToolDescriptor
        {
            Name = name,
            Description = description,
            ServiceType = serviceType,
            Method = method,
            InputSchema = schema,
            ParameterNames = paramNames,
            RequiredPermissions = permissions,
            AuthorizeData = authorizeData,
            HttpMethod = action.HttpMethod,
        };
    }

    private static string BuildDescription(MethodInfo method, ActionApiDescriptionModel action)
    {
        // v0.1 emits a mechanical description. XML-doc-based descriptions and LLM-enhanced
        // descriptions are Approach C scope. Agents work fine with mechanical names plus the
        // [McpTool(Description = "...")] override when developers want to tune a specific tool.
        return $"Invoke {method.DeclaringType?.Name}.{method.Name}.";
    }

    private static (JsonObject Schema, IReadOnlyList<string> Names) BuildInputSchema(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var names = new List<string>();

        foreach (var parameter in method.GetParameters())
        {
            // The cancellation token is supplied by the dispatcher, never by the agent.
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            names.Add(parameter.Name!);
            properties[parameter.Name!] = JsonSchemaMapper.MapParameter(parameter);

            if (JsonSchemaMapper.IsRequiredParameter(parameter))
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return (schema, names);
    }

    /// <summary>
    /// Harvest every authorization requirement the parallel HTTP endpoint would enforce.
    /// Method and class <c>[Authorize]</c> data are additive (both must pass), matching ASP.NET Core
    /// and ABP's <c>AuthorizationInterceptor</c>. Roles, schemes and policy-less (authenticated-only)
    /// attributes are kept in the returned <see cref="IAuthorizeData"/> list, not dropped.
    /// <c>[AllowAnonymous]</c> on the method clears everything, as ABP does; class-level
    /// <c>[AllowAnonymous]</c> is deliberately not honored over an <c>[Authorize]</c> (fail closed).
    /// </summary>
    internal static (IReadOnlyList<string> Permissions, IReadOnlyList<IAuthorizeData> AuthorizeData) ResolveAuthorization(
        Type serviceType,
        MethodInfo method)
    {
        if (method.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any())
        {
            return (Array.Empty<string>(), Array.Empty<IAuthorizeData>());
        }

        var authorizeData = method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
            .Concat(serviceType.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
            .ToArray();

        var permissions = authorizeData
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return (permissions, authorizeData);
    }
}
