using System.Reflection;
using AbpMcp.Attributes;
using Microsoft.Extensions.Options;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Metadata;

/// <summary>
/// Default <see cref="IApiDefinitionReader"/> implementation.
/// Uses ABP's <see cref="IApiDescriptionModelProvider"/> as the source of truth,
/// matching exactly what ABP's TS/C# proxy generators see.
/// </summary>
internal sealed class ApiDefinitionReader : IApiDefinitionReader
{
    private readonly IApiDescriptionModelProvider _provider;
    private readonly IToolDescriptorBuilder _builder;
    private readonly AbpMcpOptions _options;

    public ApiDefinitionReader(
        IApiDescriptionModelProvider provider,
        IToolDescriptorBuilder builder,
        IOptions<AbpMcpOptions> options)
    {
        _provider = provider;
        _builder = builder;
        _options = options.Value;
    }

    public IReadOnlyList<ToolDescriptor> Read()
    {
        var model = _provider.CreateApiModel(new ApplicationApiDescriptionModelRequestDto
        {
            IncludeTypes = true,
        });

        // ExposedAssemblies acts as a hard filter when registered. Empty = scan everything
        // (matching the v0.1 behaviour the docs already promise).
        var allowedAssemblies = _options.ExposedAssemblies.IsEmpty
            ? null
            : new HashSet<Assembly>(_options.ExposedAssemblies.Select(e => e.Assembly));

        var descriptors = new List<ToolDescriptor>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, module) in model.Modules)
        {
            foreach (var (_, controller) in module.Controllers)
            {
                var serviceType = ResolveType(controller.Type);
                if (serviceType is null)
                {
                    continue;
                }

                if (allowedAssemblies is not null && !allowedAssemblies.Contains(serviceType.Assembly))
                {
                    continue;
                }

                var exposeOnClass = serviceType.GetCustomAttribute<McpToolAttribute>(inherit: true);
                var hiddenOnClass = serviceType.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);
                if (hiddenOnClass is not null)
                {
                    continue;
                }

                foreach (var (_, action) in controller.Actions)
                {
                    var method = ResolveMethod(serviceType, action);
                    if (method is null)
                    {
                        continue;
                    }

                    var exposeOnMethod = method.GetCustomAttribute<McpToolAttribute>(inherit: true);
                    var hiddenOnMethod = method.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);

                    var expose = exposeOnMethod ?? exposeOnClass;
                    if (expose is null || hiddenOnMethod is not null)
                    {
                        continue;
                    }

                    var descriptor = _builder.Build(serviceType, method, action, expose, _options);
                    if (!seenNames.Add(descriptor.Name))
                    {
                        throw new AbpMcpConfigurationException(
                            $"Duplicate MCP tool name '{descriptor.Name}'. " +
                            "Override via [McpTool(Name = ...)] on the method or the containing service.");
                    }

                    descriptors.Add(descriptor);
                }
            }
        }

        return descriptors;
    }

    private static Type? ResolveType(string typeName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);

    private static MethodInfo? ResolveMethod(Type serviceType, ActionApiDescriptionModel action)
    {
        var candidates = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, action.Name, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        // Disambiguate by parameter count. ABP's action model gives us the expected count.
        return candidates.FirstOrDefault(m => m.GetParameters().Length == action.Parameters.Count);
    }
}
