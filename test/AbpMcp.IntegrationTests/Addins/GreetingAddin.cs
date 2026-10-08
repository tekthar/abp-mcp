using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.IntegrationTests.Books;
using AbpMcp.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.Addins;

/// <summary>
/// Test add-in that exercises both halves of the pipeline: it contributes a brand-new dynamic tool
/// (<c>Test_Greet</c>, backed by a handler rather than a service method) and enriches an existing
/// discovered tool (<c>Book_GetList</c>). The handler resolves <see cref="BookDbContext"/> from the
/// request scope, proving a dynamic tool runs with real per-request DI just like a service method.
/// </summary>
public sealed class GreetingAddin : IAbpMcpAddin
{
    public int Order => 50;

    public void Contribute(AbpMcpToolBuildContext context)
    {
        context.AddTool(new ToolDescriptor
        {
            Name = "Test_Greet",
            Description = "Return a greeting for the given name, plus how many books the catalog knows.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray { "name" },
            },
            ParameterNames = new[] { "name" },
            RequiredPermissions = Array.Empty<string>(),
            Handler = GreetAsync,
        });

        context.Enrich("Book_GetList", d => d with { Description = "ENRICHED: " + d.Description });
    }

    private static async Task<JsonElement> GreetAsync(AbpMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var name = invocation.Arguments.TryGetProperty("name", out var n) ? n.GetString() : null;

        // Resolve from the request scope, exactly as a service method would.
        var db = invocation.Services.GetRequiredService<BookDbContext>();
        var knownBooks = await db.Books.CountAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            greeting = $"Hello, {name}",
            knownBooks,
        });
    }
}
