using AbpMcp.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace AbpMcp.Tests;

/// <summary>
/// The list_changed session registry holds a session exactly while the SDK has its notification stream open
/// (#83). These pin the edges an end-to-end client cannot reach on purpose.
/// </summary>
public class NotificationStreamTrackerTests
{
    private readonly McpSessionRegistry _registry = new();
    private readonly McpServer _server = Substitute.For<McpServer>();

    [Fact]
    public async Task AnAcceptedStream_IsTracked_UntilItsRequestEnds()
    {
        var context = NewContext(HttpMethods.Get);
        int? whileOpen = null;

        await Run(context, async ctx =>
        {
            ctx.Features.Set(_server);
            await StartResponseAsync(ctx);
            whileOpen = _registry.Sessions.Count;
        });

        whileOpen.Should().Be(1);
        _registry.Sessions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)] // e.g. a second GET for the same session
    [InlineData(StatusCodes.Status403Forbidden)] // another user's session
    [InlineData(StatusCodes.Status404NotFound)] // an unknown or ended session
    public async Task ARefusedStream_IsNeverTracked(int status)
    {
        var context = NewContext(HttpMethods.Get);
        int? duringRequest = null;

        await Run(context, async ctx =>
        {
            ctx.Features.Set(_server);
            ctx.Response.StatusCode = status;
            await StartResponseAsync(ctx);
            duringRequest = _registry.Sessions.Count;
        });

        duringRequest.Should().Be(0);
    }

    [Fact]
    public async Task OnlyGetRequests_AreTracked()
    {
        var context = NewContext(HttpMethods.Post);
        int? duringRequest = null;

        await Run(context, async ctx =>
        {
            ctx.Features.Set(_server);
            await StartResponseAsync(ctx);
            duringRequest = _registry.Sessions.Count;
        });

        duringRequest.Should().Be(0);
    }

    [Fact]
    public async Task AResponseStartingAfterTheRequestEnded_IsNeverTracked()
    {
        var context = NewContext(HttpMethods.Get);

        await Run(context, ctx =>
        {
            ctx.Features.Set(_server);
            return Task.CompletedTask;
        });

        // The server starts a response nobody wrote to only after the request delegate returned.
        await StartResponseAsync(context);

        _registry.Sessions.Should().BeEmpty("a registration made after the request ended would never be removed");
    }

    private Task Run(HttpContext context, RequestDelegate sdkEndpoint)
    {
        var builder = new TestConventionBuilder();
        McpNotificationStreamTracker.Apply(builder, _registry);

        var endpoint = new RouteEndpointBuilder(sdkEndpoint, RoutePatternFactory.Parse("/mcp"), order: 0);
        builder.ApplyTo(endpoint);
        return endpoint.RequestDelegate!(context);
    }

    private static DefaultHttpContext NewContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Features.Set<IHttpResponseFeature>(new StartableResponseFeature());
        return context;
    }

    private static Task StartResponseAsync(HttpContext context) =>
        ((StartableResponseFeature)context.Features.Get<IHttpResponseFeature>()!).StartAsync();

    private sealed class TestConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = new();

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public void ApplyTo(EndpointBuilder endpoint) => _conventions.ForEach(convention => convention(endpoint));
    }

    /// <summary>A response feature whose <c>OnStarting</c> callbacks run when the test says the response starts.</summary>
    private sealed class StartableResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            foreach (var (callback, state) in _starting)
            {
                await callback(state);
            }

            _starting.Clear();
        }
    }
}
