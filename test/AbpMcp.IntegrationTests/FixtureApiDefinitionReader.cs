using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.IntegrationTests.Books;
using AbpMcp.Metadata;

namespace AbpMcp.IntegrationTests;

/// <summary>
/// Test fixture replacement for the production <see cref="IApiDefinitionReader"/>.
/// The real implementation depends on ABP's ApiExplorer pipeline, which requires a
/// full ASP.NET Core host that the unit test container does not provide. For these
/// tests we hand-build descriptors for the fixture services — the goal is to verify
/// the dispatcher's route + invoke + persist + permission flow, not to retest ABP's
/// own api-definition discovery (which has its own test suite upstream).
/// </summary>
internal sealed class FixtureApiDefinitionReader : IApiDefinitionReader
{
    public IReadOnlyList<ToolDescriptor> Read()
    {
        var serviceType = typeof(BookAppService);
        return new[]
        {
            BuildDescriptor(serviceType, nameof(BookAppService.CreateAsync), "Book_Create"),
            BuildDescriptor(serviceType, nameof(BookAppService.GetListAsync), "Book_GetList"),
        };
    }

    private static ToolDescriptor BuildDescriptor(Type serviceType, string methodName, string toolName)
    {
        var method = serviceType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Fixture method '{methodName}' not found on {serviceType.Name}.");

        // Build the input schema with the real JsonSchemaMapper over the actual method parameters,
        // so the fixture advertises exactly what production would (walked DTOs, enums, constraints)
        // rather than a stand-in opaque object. Mirrors ToolDescriptorBuilder.BuildInputSchema.
        var properties = new JsonObject();
        var required = new JsonArray();
        var names = new List<string>();
        foreach (var parameter in method.GetParameters())
        {
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

        return new ToolDescriptor
        {
            Name = toolName,
            Description = $"Invoke {serviceType.Name}.{methodName}.",
            ServiceType = serviceType,
            Method = method,
            InputSchema = schema,
            ParameterNames = names,
            RequiredPermissions = Array.Empty<string>(),
        };
    }
}
