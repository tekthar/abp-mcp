using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
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
        var permissions = ResolvePermissions(serviceType, method);

        return new ToolDescriptor
        {
            Name = name,
            Description = description,
            ServiceType = serviceType,
            Method = method,
            InputSchema = schema,
            ParameterNames = paramNames,
            RequiredPermissions = permissions,
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

    private static IReadOnlyList<string> ResolvePermissions(Type serviceType, MethodInfo method)
    {
        // Respect [Authorize("perm")] declarations at both levels, methods winning over class.
        var permissions = new List<string>();

        foreach (var attr in method.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(inherit: true))
        {
            if (!string.IsNullOrEmpty(attr.Policy))
            {
                permissions.Add(attr.Policy!);
            }
        }

        if (permissions.Count == 0)
        {
            foreach (var attr in serviceType.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(inherit: true))
            {
                if (!string.IsNullOrEmpty(attr.Policy))
                {
                    permissions.Add(attr.Policy!);
                }
            }
        }

        return permissions;
    }
}
