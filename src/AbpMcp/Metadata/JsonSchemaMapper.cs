using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AbpMcp.Metadata;

/// <summary>
/// Maps .NET types to JSON Schema fragments — one named branch per type family (per project
/// convention) rather than one giant switch. Complex DTOs are walked recursively into their
/// public readable properties, with a cycle guard and depth cap so self-referential graphs
/// (e.g. an AND/OR/NOT filter tree) terminate cleanly instead of recursing forever.
/// </summary>
/// <remarks>
/// The mapping choices below are driven by what real ABP applications actually put on the wire,
/// so the advertised schema is <em>true</em> — an agent that fills it produces a payload the
/// dispatcher can deserialize, with no out-of-band "actually send it like this" hints:
/// <list type="bullet">
///   <item><c>Dictionary&lt;string,T&gt;</c> becomes an <c>object</c> with <c>additionalProperties</c>,
///     NOT an array of key/value pairs — a JSON object is what the runtime binds.</item>
///   <item><c>[JsonExtensionData]</c> members open the parent object's <c>additionalProperties</c>
///     instead of appearing as a literal property that does not exist on the wire.</item>
///   <item><c>object</c>/<c>JsonElement</c>/<c>JsonNode</c> map to the permissive empty schema
///     (any JSON value) rather than an empty, misleading <c>{"type":"object"}</c>.</item>
///   <item>Property names honor <c>[JsonPropertyName]</c>, else camelCase (the System.Text.Json
///     Web default the dispatcher binds with).</item>
///   <item>Enums map to their string names; <see cref="Dispatch.AbpMcpDispatcher"/> registers a
///     <see cref="JsonStringEnumConverter"/> so those names round-trip.</item>
///   <item>DataAnnotations (<c>[Range]</c>, <c>[StringLength]</c>, <c>[MinLength]</c>,
///     <c>[MaxLength]</c>, <c>[RegularExpression]</c>, <c>[EmailAddress]</c>) surface as schema
///     constraints, so agents get guidance at schema time instead of a runtime
///     <c>AbpValidationException</c>.</item>
/// </list>
/// </remarks>
/// <remarks>
/// Public so add-ins can translate their own types (e.g. deriving an output schema from a return
/// type, or shaping the input of a dynamic tool) with the exact same rules the core uses.
/// </remarks>
public static class JsonSchemaMapper
{
    private const int MaxDepth = 8;

    /// <summary>Map a type to its JSON Schema fragment (no member-level constraints).</summary>
    public static JsonObject Map(Type type) => MapType(type, new HashSet<Type>(), depth: 0);

    /// <summary>
    /// Map a method parameter: its type schema plus any DataAnnotation constraints declared on the
    /// parameter itself (e.g. a <c>[Range]</c> bounding-box coordinate).
    /// </summary>
    public static JsonObject MapParameter(ParameterInfo parameter)
    {
        var schema = Map(parameter.ParameterType);
        ApplyValidation(schema, parameter);
        return schema;
    }

    /// <summary>
    /// True when the parameter must be supplied by the caller: it has no default and its type is
    /// non-nullable (NRT-aware for reference types). A non-optional <c>CreateFooDto input</c> body
    /// parameter is therefore correctly reported as required.
    /// </summary>
    public static bool IsRequiredParameter(ParameterInfo parameter) =>
        !parameter.HasDefaultValue && !IsNullable(parameter);

    private static JsonObject MapType(Type type, HashSet<Type> visited, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            var inner = MapType(underlying, visited, depth);
            inner["nullable"] = true;
            return inner;
        }

        if (IsAnyValue(type))
        {
            // Permissive empty schema: any JSON value is acceptable. This is what object-typed
            // filter values and free-form JSON rows really are.
            return new JsonObject();
        }

        if (TryMapScalar(type, out var scalar))
        {
            return scalar;
        }

        if (type.IsEnum)
        {
            return MapEnum(type);
        }

        if (IsDictionary(type, out var valueType) && valueType is not null)
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = MapType(valueType, visited, depth),
            };
        }

        if (IsCollection(type, out var elementType) && elementType is not null)
        {
            return new JsonObject { ["type"] = "array", ["items"] = MapType(elementType, visited, depth) };
        }

        return MapObject(type, visited, depth);
    }

    private static bool TryMapScalar(Type type, out JsonObject schema)
    {
        if (type == typeof(string))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }

        if (type == typeof(Guid))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            return true;
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            return true;
        }

        if (type == typeof(DateOnly))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "date" };
            return true;
        }

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "time" };
            return true;
        }

        if (type == typeof(Uri))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "uri" };
            return true;
        }

        if (type == typeof(bool))
        {
            schema = new JsonObject { ["type"] = "boolean" };
            return true;
        }

        if (IsIntegerLike(type))
        {
            schema = new JsonObject { ["type"] = "integer" };
            return true;
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            schema = new JsonObject { ["type"] = "number" };
            return true;
        }

        schema = null!;
        return false;
    }

    private static JsonObject MapEnum(Type type)
    {
        var values = new JsonArray();
        foreach (var name in Enum.GetNames(type))
        {
            values.Add(name);
        }

        return new JsonObject { ["type"] = "string", ["enum"] = values };
    }

    private static JsonObject MapObject(Type type, HashSet<Type> visited, int depth)
    {
        // Cycle guard and depth cap: fall back to an opaque object rather than recurse forever or
        // emit an unusably deep schema.
        if (depth >= MaxDepth || visited.Contains(type))
        {
            return new JsonObject { ["type"] = "object" };
        }

        var branch = new HashSet<Type>(visited) { type };
        var properties = new JsonObject();
        var required = new JsonArray();
        var allowsExtensionData = false;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            if (property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            {
                continue;
            }

            // A [JsonExtensionData] bag catches arbitrary unknown keys at the object root — it is not
            // a real property, so opening additionalProperties is the honest representation.
            if (property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: true))
            {
                allowsExtensionData = true;
                continue;
            }

            var name = JsonPropertyName(property);
            var propertySchema = MapType(property.PropertyType, branch, depth + 1);
            ApplyValidation(propertySchema, property);
            properties[name] = propertySchema;

            // Required-ness follows ABP's actual validation contract: a property is required only
            // when it carries [Required]. NRT non-null alone is not enforced at runtime, so an agent
            // is never forced to supply a value ABP itself treats as optional.
            if (property.IsDefined(typeof(RequiredAttribute), inherit: true))
            {
                required.Add(name);
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

        if (allowsExtensionData)
        {
            schema["additionalProperties"] = true;
        }

        return schema;
    }

    /// <summary>
    /// Translate DataAnnotation validation attributes into JSON Schema constraints. Kept in one
    /// place so the set of honored annotations is easy to see and extend.
    /// </summary>
    private static void ApplyValidation(JsonObject schema, ICustomAttributeProvider member)
    {
        var jsonType = schema["type"]?.GetValue<string>();

        if (GetAttribute<RangeAttribute>(member) is { } range)
        {
            if (TryToDouble(range.Minimum, out var min))
            {
                schema["minimum"] = min;
            }

            if (TryToDouble(range.Maximum, out var max))
            {
                schema["maximum"] = max;
            }
        }

        if (GetAttribute<StringLengthAttribute>(member) is { } stringLength)
        {
            if (stringLength.MinimumLength > 0)
            {
                schema["minLength"] = stringLength.MinimumLength;
            }

            schema["maxLength"] = stringLength.MaximumLength;
        }

        if (GetAttribute<MinLengthAttribute>(member) is { } minLength)
        {
            schema[jsonType == "array" ? "minItems" : "minLength"] = minLength.Length;
        }

        if (GetAttribute<MaxLengthAttribute>(member) is { } maxLength)
        {
            schema[jsonType == "array" ? "maxItems" : "maxLength"] = maxLength.Length;
        }

        if (jsonType == "string" && GetAttribute<RegularExpressionAttribute>(member) is { } regex)
        {
            schema["pattern"] = regex.Pattern;
        }

        if (jsonType == "string" && GetAttribute<EmailAddressAttribute>(member) is not null)
        {
            schema["format"] = "email";
        }
    }

    private static string JsonPropertyName(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        if (attribute is not null && !string.IsNullOrEmpty(attribute.Name))
        {
            return attribute.Name;
        }

        // System.Text.Json Web defaults serialize as camelCase; mirror that so the advertised
        // property names match what the dispatcher will actually bind.
        var name = property.Name;
        if (name.Length > 0 && char.IsUpper(name[0]))
        {
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        return name;
    }

    private static bool IsAnyValue(Type type) =>
        type == typeof(object) ||
        type == typeof(System.Text.Json.JsonElement) ||
        type == typeof(JsonNode) ||
        type == typeof(JsonObject) ||
        type == typeof(JsonArray) ||
        type == typeof(JsonValue) ||
        type == typeof(System.Text.Json.JsonDocument);

    private static bool IsIntegerLike(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) ||
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) ||
        t == typeof(ulong) || t == typeof(ushort);

    private static bool IsDictionary(Type t, out Type? valueType)
    {
        foreach (var candidate in Interfaces(t))
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                valueType = candidate.GetGenericArguments()[1];
                return true;
            }
        }

        valueType = null;
        return false;
    }

    private static bool IsCollection(Type t, out Type? elementType)
    {
        if (t == typeof(string))
        {
            elementType = null;
            return false;
        }

        if (t.IsArray)
        {
            elementType = t.GetElementType();
            return true;
        }

        foreach (var candidate in Interfaces(t))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }

    private static IEnumerable<Type> Interfaces(Type t)
    {
        if (t.IsInterface)
        {
            yield return t;
        }

        foreach (var iface in t.GetInterfaces())
        {
            yield return iface;
        }
    }

    private static bool IsNullable(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type.IsValueType)
        {
            return Nullable.GetUnderlyingType(type) is not null;
        }

        // Reference type: honor the declared nullable-reference-type annotation.
        var nullability = new NullabilityInfoContext().Create(parameter);
        return nullability.WriteState == NullabilityState.Nullable;
    }

    private static T? GetAttribute<T>(ICustomAttributeProvider member) where T : Attribute =>
        member.GetCustomAttributes(typeof(T), inherit: true).OfType<T>().FirstOrDefault();

    private static bool TryToDouble(object? value, out double result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }

        try
        {
            result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
