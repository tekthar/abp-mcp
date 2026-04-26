using System.Text.Json;
using AbpMcp.Dispatch;
using AbpMcp.IntegrationTests.Books;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AbpMcp.IntegrationTests.Tools;

/// <summary>
/// The friend's testing pattern: seed a known DB state, invoke an MCP tool through
/// the dispatcher, then query the database directly to confirm the side effect
/// landed exactly where it should have. Each test owns its own scope and its own
/// in-memory database, so tests can run in parallel without cross-talk.
/// </summary>
public sealed class DispatcherInvocationTests : AbpMcpIntegrationTestBase
{
    [Fact]
    public async Task RegistryDiscoversBookAppServiceTools()
    {
        var registry = ServiceProvider.GetRequiredService<IDynamicMcpToolRegistry>();
        registry.Initialize();

        var names = registry.Tools.Select(t => t.Name).ToArray();

        names.Should().Contain("Book_Create");
        names.Should().Contain("Book_GetList");
    }

    [Fact]
    public async Task BookCreate_PersistsRowToDatabase()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            // ARRANGE: empty DB (each test gets its own in-memory instance via the module).
            var db = services.GetRequiredService<BookDbContext>();
            (await db.Books.CountAsync()).Should().Be(0);

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
            registry.Initialize();
            registry.TryGetByName("Book_Create", out var descriptor).Should().BeTrue();

            var arguments = JsonDocument.Parse("""
                {
                    "input": {
                        "title": "The Pragmatic Programmer",
                        "author": "Hunt & Thomas",
                        "year": 1999
                    }
                }
                """).RootElement;

            // ACT: invoke through the dispatcher, the same path agent calls take.
            var resultJson = await dispatcher.InvokeAsync(descriptor!, arguments, CancellationToken.None);

            // ASSERT — friend's rule: don't trust the return value, query the DB.
            var rowsAfter = await db.Books.AsNoTracking().ToListAsync();
            rowsAfter.Should().HaveCount(1);
            rowsAfter[0].Title.Should().Be("The Pragmatic Programmer");
            rowsAfter[0].Author.Should().Be("Hunt & Thomas");
            rowsAfter[0].Year.Should().Be(1999);

            // The dispatcher's structured result should reflect what was stored.
            resultJson.GetProperty("title").GetString().Should().Be("The Pragmatic Programmer");
            resultJson.GetProperty("year").GetInt32().Should().Be(1999);
            resultJson.GetProperty("id").GetGuid().Should().Be(rowsAfter[0].Id);

            return true;
        });
    }

    [Fact]
    public async Task BookGetList_ReturnsSeededRows()
    {
        await RunAsAuthenticatedAsync(async services =>
        {
            // ARRANGE: seed the DB before the agent is allowed to call.
            var db = services.GetRequiredService<BookDbContext>();
            db.Books.AddRange(
                new Book { Id = Guid.NewGuid(), Title = "Book A", Author = "Author A", Year = 2020 },
                new Book { Id = Guid.NewGuid(), Title = "Book B", Author = "Author B", Year = 2021 });
            await db.SaveChangesAsync();

            var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
            var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
            registry.Initialize();
            registry.TryGetByName("Book_GetList", out var descriptor).Should().BeTrue();

            // ACT
            using var emptyArgs = JsonDocument.Parse("{}");
            var resultJson = await dispatcher.InvokeAsync(descriptor!, emptyArgs.RootElement, CancellationToken.None);

            // ASSERT: dispatcher's result mirrors the seeded rows.
            resultJson.ValueKind.Should().Be(JsonValueKind.Array);
            resultJson.GetArrayLength().Should().Be(2);
            var titles = resultJson.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToArray();
            titles.Should().BeEquivalentTo("Book A", "Book B");

            return true;
        });
    }

    [Fact]
    public async Task DisabledTool_ShortCircuitsWithCode()
    {
        // Configure the kill switch, then verify the dispatcher refuses BEFORE invoking the service.
        await RunAsAuthenticatedAsync(async services =>
        {
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AbpMcpOptions>>();
            options.CurrentValue.DisabledTools.Add("Book_Create");

            try
            {
                var dispatcher = services.GetRequiredService<IAbpMcpDispatcher>();
                var registry = services.GetRequiredService<IDynamicMcpToolRegistry>();
                registry.Initialize();
                registry.TryGetByName("Book_Create", out var descriptor).Should().BeTrue();

                using var args = JsonDocument.Parse("""{"input":{"title":"x","author":"y","year":2024}}""");

                var act = async () => await dispatcher.InvokeAsync(descriptor!, args.RootElement, CancellationToken.None);

                var thrown = await act.Should().ThrowAsync<AbpMcpToolException>();
                thrown.Which.Code.Should().Be("DISABLED");

                // The DB must remain pristine — kill switch fires before service invocation.
                var db = services.GetRequiredService<BookDbContext>();
                (await db.Books.CountAsync()).Should().Be(0);
            }
            finally
            {
                options.CurrentValue.DisabledTools.Remove("Book_Create");
            }

            return true;
        });
    }
}
