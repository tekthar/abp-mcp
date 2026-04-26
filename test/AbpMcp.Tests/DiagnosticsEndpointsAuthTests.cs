using AbpMcp.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AbpMcp.Tests;

/// <summary>
/// Regression coverage for the v0.1 fix where <c>/mcp/_discover</c> and <c>/mcp/_explain</c>
/// inherited no authorization metadata even when the host had <c>AllowAnonymous = false</c>,
/// silently leaking the full tool surface and parameter schemas to anonymous callers.
/// The fix made the auth posture explicit: the diagnostic endpoints now always follow the
/// same posture as the MCP endpoint itself.
/// </summary>
public class DiagnosticsEndpointsAuthTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Map_AppliesAuthorizationOnlyWhenRequested(bool requireAuthorization)
    {
        var endpoints = BuildRouteBuilder();

        AbpMcpDiagnosticsEndpoints.Map(endpoints, "/mcp", requireAuthorization);

        var routes = endpoints.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var discover = routes.Should().ContainSingle(e => e.RoutePattern.RawText == "/mcp/_discover").Subject;
        var explain = routes.Should().ContainSingle(e => e.RoutePattern.RawText == "/mcp/_explain").Subject;

        discover.Metadata.OfType<IAuthorizeData>().Any().Should().Be(
            requireAuthorization,
            because: "the diagnostic endpoints must mirror the MCP endpoint's auth posture");
        explain.Metadata.OfType<IAuthorizeData>().Any().Should().Be(
            requireAuthorization,
            because: "the diagnostic endpoints must mirror the MCP endpoint's auth posture");
    }

    private static TestEndpointRouteBuilder BuildRouteBuilder()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        var sp = services.BuildServiceProvider();
        return new TestEndpointRouteBuilder(sp);
    }

    /// <summary>
    /// Minimal <see cref="IEndpointRouteBuilder"/> for tests. Captures route data sources
    /// the same way <see cref="WebApplication"/> would, without spinning up a real host.
    /// </summary>
    private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public TestEndpointRouteBuilder(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

        public IServiceProvider ServiceProvider { get; }

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public static readonly NullLoggerProvider Instance = new();

        public ILogger CreateLogger(string categoryName) => new NullLogger();

        public void Dispose() { }

        private sealed class NullLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
