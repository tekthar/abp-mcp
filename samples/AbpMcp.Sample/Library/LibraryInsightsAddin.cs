using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.Sample.Library;

/// <summary>
/// Demonstrates a <em>dynamic</em> add-in: a tool that is not a one-to-one wrapper over an
/// application-service method. <c>Library_Overview</c> aggregates across the whole catalog in a
/// single handler, resolving <see cref="LibraryDbContext"/> from the request scope exactly as a
/// service would. Registered in <c>LibrarySampleModule</c> via
/// <c>services.AddSingleton&lt;IAbpMcpAddin, LibraryInsightsAddin&gt;()</c>.
/// </summary>
public sealed class LibraryInsightsAddin : IAbpMcpAddin
{
    // Discovery band: contribute the tool before metadata/schema add-ins run.
    public int Order => 50;

    public void Contribute(AbpMcpToolBuildContext context)
    {
        context.AddTool(new ToolDescriptor
        {
            Name = "Library_Overview",
            Description = "Aggregate catalog counts: titles, editions, members, and active loans.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
            },
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = Array.Empty<string>(),
            Handler = OverviewAsync,
        });
    }

    private static async Task<JsonElement> OverviewAsync(AbpMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var db = invocation.Services.GetRequiredService<LibraryDbContext>();

        var overview = new
        {
            titles = await db.Titles.CountAsync(cancellationToken).ConfigureAwait(false),
            editions = await db.Editions.CountAsync(cancellationToken).ConfigureAwait(false),
            members = await db.Members.CountAsync(cancellationToken).ConfigureAwait(false),
            activeLoans = await db.Loans.CountAsync(l => l.Status == LoanStatus.Active, cancellationToken).ConfigureAwait(false),
        };

        return JsonSerializer.SerializeToElement(overview);
    }
}
