using System.Reflection;
using System.Text.Json.Nodes;
using AbpMcp.Addins.XmlDocumentation;
using AbpMcp.Attributes;

namespace AbpMcp.Addins;

/// <summary>
/// Built-in add-in that fills tool and parameter descriptions from the host's XML documentation
/// comments. Runs in the metadata band. A silent no-op when
/// <see cref="AbpMcpOptions.UseXmlDocumentation"/> is false or no XML doc file is present.
/// </summary>
/// <remarks>
/// Description quality is the single biggest lever on how well an agent uses a tool, and ABP services
/// already carry <c>&lt;summary&gt;</c>/<c>&lt;param&gt;</c> docs — this surfaces them for free instead of
/// leaving the mechanical "Invoke Service.Method." fallback. An explicit
/// <c>[McpTool(Description = ...)]</c> (on the method or its service) always wins; this add-in only
/// fills in where the author did not override. Dynamic (handler-backed) tools are skipped — they have
/// no method to document.
/// </remarks>
internal sealed class XmlDocDescriptionAddin : IAbpMcpAddin
{
    public int Order => 200;

    public void Contribute(AbpMcpToolBuildContext context)
    {
        if (!context.Options.UseXmlDocumentation)
        {
            return;
        }

        var methodTools = context.Tools.Where(t => t.Method is not null).ToArray();
        if (methodTools.Length == 0)
        {
            return;
        }

        var store = XmlDocumentationStore.LoadForAssemblies(methodTools.SelectMany(RelevantAssemblies));

        foreach (var tool in methodTools)
        {
            if (HasExplicitDescription(tool))
            {
                continue;
            }

            var doc = store.GetMethod(tool.Method!);
            if (doc is null)
            {
                continue;
            }

            context.Enrich(tool.Name, d =>
            {
                if (doc.Parameters.Count > 0)
                {
                    ApplyParameterDescriptions(d.InputSchema, doc.Parameters);
                }

                return string.IsNullOrWhiteSpace(doc.Summary)
                    ? d
                    : d with { Description = doc.Summary! };
            });
        }
    }

    private static IEnumerable<Assembly> RelevantAssemblies(Metadata.ToolDescriptor tool)
    {
        if (tool.Method?.DeclaringType is not null)
        {
            yield return tool.Method.DeclaringType.Assembly;
        }

        if (tool.ServiceType is null)
        {
            yield break;
        }

        yield return tool.ServiceType.Assembly;
        foreach (var iface in tool.ServiceType.GetInterfaces())
        {
            yield return iface.Assembly;
        }
    }

    private static bool HasExplicitDescription(Metadata.ToolDescriptor tool)
    {
        var onMethod = tool.Method!.GetCustomAttribute<McpToolAttribute>(inherit: true);
        if (!string.IsNullOrEmpty(onMethod?.Description))
        {
            return true;
        }

        var onClass = tool.ServiceType?.GetCustomAttribute<McpToolAttribute>(inherit: true);
        return !string.IsNullOrEmpty(onClass?.Description);
    }

    private static void ApplyParameterDescriptions(JsonObject inputSchema, IReadOnlyDictionary<string, string> parameters)
    {
        if (inputSchema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (name, description) in parameters)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            if (properties[name] is JsonObject property && property["description"] is null)
            {
                property["description"] = description;
            }
        }
    }
}
