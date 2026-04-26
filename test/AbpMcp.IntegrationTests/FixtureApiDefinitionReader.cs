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
            BuildDescriptor(serviceType, nameof(BookAppService.CreateAsync), "Book_Create",
                inputs: new[]
                {
                    ("input", typeof(CreateBookDto), required: true),
                }),
            BuildDescriptor(serviceType, nameof(BookAppService.GetListAsync), "Book_GetList",
                inputs: Array.Empty<(string, Type, bool)>()),
        };
    }

    private static ToolDescriptor BuildDescriptor(
        Type serviceType,
        string methodName,
        string toolName,
        IReadOnlyList<(string Name, Type Type, bool Required)> inputs)
    {
        var method = serviceType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Fixture method '{methodName}' not found on {serviceType.Name}.");

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, isRequired) in inputs)
        {
            properties[name] = type == typeof(CreateBookDto)
                ? new JsonObject { ["type"] = "object" }
                : new JsonObject { ["type"] = "string" };
            if (isRequired)
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

        return new ToolDescriptor
        {
            Name = toolName,
            Description = $"Invoke {serviceType.Name}.{methodName}.",
            ServiceType = serviceType,
            Method = method,
            InputSchema = schema,
            ParameterNames = inputs.Select(i => i.Name).ToArray(),
            RequiredPermissions = Array.Empty<string>(),
        };
    }
}
