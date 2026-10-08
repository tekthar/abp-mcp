using AbpMcp.Metadata;

namespace AbpMcp.Addins;

/// <summary>
/// Built-in add-in that advertises an output schema for every method-backed tool, derived from the
/// method's return type. Runs in the schema band and is a no-op when
/// <see cref="AbpMcpOptions.IncludeOutputSchema"/> is false or a tool already carries an output schema.
/// </summary>
/// <remarks>
/// <para>
/// Also serves as the reference example of an enriching add-in: it reads each tool, computes new
/// metadata, and folds it back with <see cref="AbpMcpToolBuildContext.Enrich"/>.
/// </para>
/// <para>
/// The return type is unwrapped through <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c>, then mapped by
/// <see cref="JsonSchemaMapper"/> like any other type. ABP result wrappers need no special-casing:
/// <c>PagedResultDto&lt;T&gt;</c> walks into <c>{ items: [T], totalCount: integer }</c> and
/// <c>IReadOnlyList&lt;T&gt;</c> into an array of <c>T</c>.
/// </para>
/// </remarks>
internal sealed class OutputSchemaAddin : IAbpMcpAddin
{
    public int Order => 500;

    public void Contribute(AbpMcpToolBuildContext context)
    {
        if (!context.Options.IncludeOutputSchema)
        {
            return;
        }

        // Snapshot names first: Enrich replaces descriptors in place, and iterating names keeps this
        // robust regardless of how the pipeline evolves.
        foreach (var name in context.Tools.Select(t => t.Name).ToArray())
        {
            var tool = context.FindTool(name);
            if (tool?.Method is null || tool.OutputSchema is not null)
            {
                continue;
            }

            var resultType = UnwrapReturnType(tool.Method.ReturnType);
            if (resultType is null)
            {
                continue;
            }

            var schema = JsonSchemaMapper.Map(resultType);
            context.Enrich(name, d => d with { OutputSchema = schema });
        }
    }

    /// <summary>
    /// Reduce a method return type to the type actually returned to the caller. Awaitable wrappers
    /// are unwrapped; non-generic <c>Task</c>/<c>ValueTask</c> and <c>void</c> yield no schema.
    /// </summary>
    private static Type? UnwrapReturnType(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return null;
        }

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }
}
