using System.Security.Claims;
using AbpMcp.IntegrationTests.Books;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities.Events.Distributed;
using Volo.Abp.Modularity;

namespace AbpMcp.IntegrationTests;

/// <summary>
/// ABP startup module for the integration test suite.
/// Wires Autofac (required by AbpIntegratedTest), ABP's MVC stack (so the api-definition
/// provider is available to the reader), abp-mcp itself, and an EF Core in-memory
/// <see cref="BookDbContext"/> with a deterministic database name per test run.
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpAuthorizationModule),
    typeof(AbpBackgroundJobsAbstractionsModule),
    typeof(AbpMcpModule))]
public sealed class AbpMcpTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        // Defensive test setup borrowed from a vetted ABP integration test pattern:
        //   - permissions short-circuited so tests don't need policy plumbing
        //   - background jobs off so write paths don't kick off async work
        //   - distributed entity events off so DB writes don't fan out to handlers
        services.AddAlwaysAllowAuthorization();

        Configure<AbpBackgroundJobOptions>(options =>
        {
            options.IsJobExecutionEnabled = false;
        });

        Configure<AbpDistributedEntityEventOptions>(options =>
        {
            options.AutoEventSelectors.Clear();
        });

        // Replace the production reader (which depends on ABP's ApiExplorer pipeline,
        // requiring a full ASP.NET Core host) with a fixture that hand-builds descriptors
        // for the test services. We're testing the dispatcher's route+invoke+persist flow,
        // not ABP's api-definition discovery.
        services.Replace(ServiceDescriptor.Singleton<IApiDefinitionReader, FixtureApiDefinitionReader>());

        // Unique DB per module instance so tests don't bleed.
        var dbName = $"abp-mcp-tests-{Guid.NewGuid():N}";
        services.AddDbContext<BookDbContext>(opts => opts.UseInMemoryDatabase(dbName));

        // Tests run without a real HTTP request. Provide a stand-in HttpContext that:
        //   1) carries an authenticated principal (so the dispatcher's auth check has a User)
        //   2) exposes the test's IServiceProvider as RequestServices (so the dispatcher
        //      resolves IBookAppService from the same scope as the test)
        services.AddSingleton<IHttpContextAccessor, TestHttpContextAccessor>();

        // Tell abp-mcp to scan only this assembly. Honors AbpMcpOptions.ExposedAssemblies.
        Configure<AbpMcpOptions>(opts =>
        {
            opts.ExposedAssemblies.Create(typeof(AbpMcpTestModule).Assembly);
            opts.AllowAnonymous = true; // tests do not run real auth middleware
            opts.RequireAtLeastOneTool = false;
        });
    }
}

/// <summary>
/// Test-time <see cref="IHttpContextAccessor"/> that hands the dispatcher a fully
/// formed <see cref="HttpContext"/> rooted in the test's DI scope, with a configurable
/// <see cref="ClaimsPrincipal"/> for permission scenarios.
/// </summary>
public sealed class TestHttpContextAccessor : IHttpContextAccessor
{
    private readonly IServiceProvider _services;

    public TestHttpContextAccessor(IServiceProvider services) => _services = services;

    public HttpContext? HttpContext { get; set; }

    /// <summary>Build a fresh HttpContext bound to the given request scope and user.</summary>
    public HttpContext PushAuthenticatedContext(IServiceProvider scopedServices, ClaimsPrincipal user)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = scopedServices,
            User = user,
        };
        HttpContext = ctx;
        return ctx;
    }
}
