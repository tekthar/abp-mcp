using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Metadata;

/// <summary>
/// Default implementation of <see cref="IToolDescriptorBuilder"/>.
/// Produces tool names of the form <c>{ServiceShortName}_{MethodShortName}</c>,
/// pulls descriptions from XML docs or the <c>[McpTool(Description = ...)]</c> override,
/// and emits JSON Schema for the input object by translating the method's parameters.
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
            if (IsIgnorable(parameter.ParameterType))
            {
                continue;
            }

            names.Add(parameter.Name!);
            properties[parameter.Name!] = JsonSchemaMapper.Map(parameter.ParameterType);
            if (!parameter.HasDefaultValue && !IsNullable(parameter.ParameterType))
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

    private static bool IsIgnorable(Type t) =>
        t == typeof(CancellationToken);

    private static bool IsNullable(Type t) =>
        !t.IsValueType || Nullable.GetUnderlyingType(t) is not null;

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

/// <summary>
/// Maps .NET parameter types to JSON Schema fragments. Kept internal and simple for v0.1.
/// Full coverage (collections, complex DTOs with recursion, polymorphism) lives in the
/// test matrix and will be extended deliberately, type family by type family.
/// </summary>
internal static class JsonSchemaMapper
{
    public static JsonObject Map(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            var inner = Map(underlying);
            inner["nullable"] = true;
            return inner;
        }

        if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            var result = new JsonObject { ["type"] = "string" };
            if (type == typeof(Guid)) result["format"] = "uuid";
            else if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) result["format"] = "date-time";
            return result;
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (IsIntegerLike(type))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var name in Enum.GetNames(type))
            {
                values.Add(name);
            }

            return new JsonObject { ["type"] = "string", ["enum"] = values };
        }

        if (IsCollection(type, out var elementType) && elementType is not null)
        {
            return new JsonObject { ["type"] = "array", ["items"] = Map(elementType) };
        }

        // Fallback for complex types. v0.1 emits an opaque object schema; proper DTO walk is v1.0.
        return new JsonObject { ["type"] = "object" };
    }

    private static bool IsIntegerLike(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) ||
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) ||
        t == typeof(ulong) || t == typeof(ushort);

    private static bool IsCollection(Type t, out Type? elementType)
    {
        if (t.IsArray)
        {
            elementType = t.GetElementType();
            return true;
        }

        // The type itself might BE IEnumerable<T> (e.g. a method parameter typed IEnumerable<string>).
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = t.GetGenericArguments()[0];
            return true;
        }

        foreach (var iface in t.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }
}
