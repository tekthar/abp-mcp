using System.Text.Json;
using System.Text.Json.Nodes;
using AbpMcp.Attributes;
using AbpMcp.Authorization;
using AbpMcp.Dispatch;
using AbpMcp.Gateway;
using AbpMcp.Metadata;
using AbpMcp.Registration;
using AbpMcp.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AbpMcp.Tests;

/// <summary>
/// #119: the gateway tools. Search and describe go through the same visibility gate as <c>tools/list</c> (a real
/// <see cref="AbpMcpToolVisibility"/> here), and calls go through <see cref="IAbpMcpToolInvoker"/>.
/// </summary>
public class ToolGatewayTests
{
    private const string Forbidden = "Member_Delete";
    private const string Disabled = "Member_Purge";

    private readonly RecordingInvoker _invoker = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal) { Disabled };

    [Fact]
    public void Gateway_IsOff_WithoutOnDemandTools_AndOn_WithOne()
    {
        var off = NewGateway([Tool("Member_Get", "GET")]);
        var on = NewGateway([Tool("Member_Get", "GET"), Tool("Member_Suspend", "POST", onDemand: true)]);

        off.GetListedTools().Should().BeEmpty();
        off.Handles(AbpMcpGatewayToolNames.Search).Should().BeFalse();
        on.GetListedTools().Select(t => t.Name).Should().Equal(AbpMcpGatewayToolNames.All);
        on.Handles(AbpMcpGatewayToolNames.Search).Should().BeTrue();
        on.Handles("Member_Suspend").Should().BeFalse("only gateway names are the gateway's to handle");
    }

    [Fact]
    public async Task Search_ReturnsOnlyWhatTheCallerMaySee_WithTheItemShape()
    {
        var gateway = NewGateway(MemberTools());

        var result = await CallAsync(gateway, AbpMcpGatewayToolNames.Search, new { query = "member" });

        var tools = result["tools"]!.AsArray();
        tools.Select(Name).Should().BeEquivalentTo("Member_Get", "Member_Suspend")
            .And.NotContain([Forbidden, Disabled], "search applies the tools/list gate");
        result["total_matches"]!.GetValue<int>().Should().Be(2);

        var suspend = tools.Single(t => Name(t) == "Member_Suspend")!.AsObject();
        suspend.Select(p => p.Key).Should().Equal("name", "description", "http_method", "read_only", "listing");
        suspend["description"]!.GetValue<string>().Should().Be("Suspend a member with a reason.");
        suspend["http_method"]!.GetValue<string>().Should().Be("POST");
        suspend["read_only"]!.GetValue<bool>().Should().BeFalse();
        suspend["listing"]!.GetValue<string>().Should().Be("on_demand");
        tools.Single(t => Name(t) == "Member_Get")!["read_only"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(3, 3)]
    [InlineData(500, 50)]
    [InlineData(0, 1)]
    public async Task Search_Limit_DefaultsToTen_AndIsClampedToFifty(int? limit, int expected)
    {
        var many = Enumerable.Range(0, 60).Select(i => Tool($"Widget_Op{i:00}", "POST", onDemand: true)).ToList();
        var gateway = NewGateway(many);

        var result = await CallAsync(gateway, AbpMcpGatewayToolNames.Search, new { query = "widget", limit });

        result["tools"]!.AsArray().Should().HaveCount(expected);
        result["total_matches"]!.GetValue<int>().Should().Be(60);
    }

    [Fact]
    public async Task Search_WithNoMatch_ReturnsAHint()
    {
        var result = await CallAsync(NewGateway(MemberTools()), AbpMcpGatewayToolNames.Search, new { query = "spaceship" });

        result["tools"]!.AsArray().Should().BeEmpty();
        result["hint"]!.GetValue<string>().Should().StartWith("No tool matched.");
    }

    [Fact]
    public async Task Search_RefusesAQueryLongerThanTheCap_BeforeScoringAnything()
    {
        // A search needs no permission, so its cost must not grow with the request: a long query is refused outright.
        var query = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"w{i:000}"));
        query.Length.Should().BeGreaterThan(GatewayToolDefinitions.MaxSearchQueryLength);

        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.Search, Json(new { query }), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().StartWith("[VALIDATION_ERROR]").And.Contain("at most 512 characters");
        GatewayToolDefinitions.Tools[0].InputSchema["properties"]!["query"]!["maxLength"]!.GetValue<int>()
            .Should().Be(GatewayToolDefinitions.MaxSearchQueryLength, "the cap is advertised in the schema");
    }

    [Fact]
    public async Task Search_StopsWhenTheRequestIsCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.Search, Json(new { query = "member" }), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Describe_ReturnsProtocolShapedTools_AndReportsUnknownAndForbiddenAlikeAsNotFound()
    {
        var gateway = NewGateway(MemberTools());

        var result = await CallAsync(gateway, AbpMcpGatewayToolNames.Describe,
            new { names = new[] { "Member_Suspend", Forbidden, "Nope", Disabled, "Member_Suspend" } });

        var tool = result["tools"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        tool.Select(p => p.Key).Should().Equal("name", "description", "inputSchema", "outputSchema");
        tool["inputSchema"]!["properties"]!["reason"]!["type"]!.GetValue<string>().Should().Be("string");
        tool["outputSchema"]!["properties"]!["result"]!["type"]!.GetValue<string>()
            .Should().Be("array", "a collection result is advertised wrapped, as tools/list advertises it");
        result["not_found"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(Forbidden, "Nope", Disabled);
    }

    [Fact]
    public async Task CallTool_ReturnsTheInvokersOutcomeUnchanged()
    {
        var gateway = NewGateway(MemberTools());

        var outcome = await gateway.CallAsync(AbpMcpGatewayToolNames.Call,
            Json(new { name = "Member_Suspend", arguments = new { reason = "late" } }), CancellationToken.None);

        outcome.Should().BeSameAs(_invoker.Outcomes["Member_Suspend"]);
        _invoker.Calls.Should().ContainSingle().Which.Should().Be(("Member_Suspend", """{"reason":"late"}"""));
    }

    [Theory]
    [InlineData("""{"name":"mcp_call_tools","arguments":{}}""", "is a gateway tool")]
    [InlineData("""{"name":"mcp_search_tools"}""", "is a gateway tool")]
    [InlineData("""{"name":"Member_Get","arguments":"{}"}""", "'arguments' must be a JSON object")]
    [InlineData("""{"arguments":{}}""", "'name' is required")]
    [InlineData("""{"name":" Member_Get"}""", "without spaces or control characters")]
    [InlineData("""{"name":"Member_Get\u0085"}""", "without spaces or control characters")]
    [InlineData("""{"name":"\uD800"}""", "'name' is required")]
    public async Task CallTool_RefusesBadArguments_WithoutCallingAnything(string arguments, string message)
    {
        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.Call, Parse(arguments), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().StartWith("[VALIDATION_ERROR]").And.Contain(message);
        _invoker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CallTools_Sequential_StopsAtTheFirstError_AndSkipsTheRest()
    {
        var result = await CallAsync(NewGateway(MemberTools()), AbpMcpGatewayToolNames.CallMany, new
        {
            calls = new object[] { new { name = "Member_Get" }, new { name = "Member_Fail" }, new { name = "Member_Suspend" } },
        });

        result["mode"]!.GetValue<string>().Should().Be("sequential");
        Statuses(result).Should().Equal("ok", "error", "skipped");
        (result["succeeded"]!.GetValue<int>(), result["failed"]!.GetValue<int>(), result["skipped"]!.GetValue<int>())
            .Should().Be((1, 1, 1));
        _invoker.Calls.Select(c => c.Name).Should().Equal("Member_Get", "Member_Fail");
    }

    [Fact]
    public async Task CallTools_Parallel_RunsEveryCall_AndReportsEachResult()
    {
        var result = await CallAsync(NewGateway(MemberTools()), AbpMcpGatewayToolNames.CallMany, new
        {
            calls = new object[] { new { name = "Member_Get" }, new { name = "Member_Fail" }, new { name = "Member_Suspend" } },
            mode = "parallel",
        });

        Statuses(result).Should().Equal("ok", "error", "ok");
        _invoker.Calls.Select(c => c.Name).Should().Equal("Member_Get", "Member_Fail", "Member_Suspend");

        var results = result["results"]!.AsArray();
        var ok = results[0]!.AsObject();
        ok.Select(p => p.Key).Should().Equal("name", "status", "isError", "structuredContent");
        ok["structuredContent"]!["id"]!.GetValue<int>().Should().Be(7);
        var error = results[1]!.AsObject();
        error.Select(p => p.Key).Should().Equal("name", "status", "isError", "content");
        error["isError"]!.GetValue<bool>().Should().BeTrue();
        error["content"]![0]!["type"]!.GetValue<string>().Should().Be("text");
        error["content"]![0]!["text"]!.GetValue<string>().Should().Be("[LIBRARY:BOOM] It failed.");
        results[2]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("42", "a result with no structured content is reported as content");
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("parallel")]
    public async Task CallTools_StartsNoCallAfterTheRequestIsCancelled(string mode)
    {
        using var request = new CancellationTokenSource();
        _invoker.OnInvoke = request.Cancel;   // the client cancels while the first call runs

        var act = () => NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.CallMany,
            Json(new { calls = new[] { new { name = "Member_Get" }, new { name = "Member_Suspend" }, new { name = "Member_Get" } }, mode }),
            request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("a cancelled request is the client's own cancellation");
        _invoker.Calls.Select(c => c.Name).Should().Equal(["Member_Get"], "a cancelled batch starts no further call");
    }

    [Fact]
    public async Task CallTools_KeepsTheResultsOfCallsThatRan_WhenALaterCallThrows()
    {
        // A failed kill-switch read or an authorization handler that throws escapes the invoker; the calls before it
        // already ran and committed, so the batch still reports them.
        var parallel = await CallAsync(NewGateway(MemberTools()), AbpMcpGatewayToolNames.CallMany, new
        {
            calls = new object[] { new { name = "Member_Get" }, new { name = RecordingInvoker.Throws }, new { name = "Member_Suspend" } },
            mode = "parallel",
        });

        Statuses(parallel).Should().Equal("ok", "error", "ok");
        parallel["results"]![1]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Be($"[INTERNAL] Tool '{RecordingInvoker.Throws}' failed with an internal error; it may have run.");

        var sequential = await CallAsync(NewGateway(MemberTools()), AbpMcpGatewayToolNames.CallMany, new
        {
            calls = new object[] { new { name = "Member_Get" }, new { name = RecordingInvoker.Throws }, new { name = "Member_Suspend" } },
        });

        Statuses(sequential).Should().Equal("ok", "error", "skipped");
    }

    [Fact]
    public async Task CallTools_ReportsAsDeepAResult_AsADirectCallCanReturn()
    {
        // The dispatcher serializes up to 64 levels; wrapped in the batch envelope the result sits a few levels deeper.
        JsonNode deep = 1;
        for (var level = 0; level < 63; level++)
        {
            deep = new JsonObject { ["c"] = deep };
        }

        var element = JsonSerializer.SerializeToElement(deep, new JsonSerializerOptions { MaxDepth = 64 });
        _invoker.Outcomes["Member_Get"] = AbpMcpToolCallOutcome.Success(element.GetRawText(), element);

        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.CallMany,
            Json(new { calls = new[] { new { name = "Member_Get" } } }), CancellationToken.None);

        outcome.IsError.Should().BeFalse(outcome.Text);
        var result = JsonNode.Parse(outcome.Text, documentOptions: new JsonDocumentOptions { MaxDepth = 128 })!;
        Statuses(result).Should().Equal("ok");
        outcome.StructuredContent.Should().NotBeNull();
    }

    [Fact]
    public async Task CallTools_RunsEachCallInADiScopeOfItsOwn()
    {
        // State one call leaves in a scoped service (an injected DbContext's change tracker) must not reach the next.
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<ScopeProbe>();
        services.AddScoped<IAbpMcpToolInvoker, ProbingInvoker>();
        services.AddSingleton(new List<ScopeProbe>());
        await using var root = services.BuildServiceProvider();
        await using var request = root.CreateAsyncScope();
        var accessor = root.GetRequiredService<IHttpContextAccessor>();
        var http = new DefaultHttpContext { RequestServices = request.ServiceProvider };
        accessor.HttpContext = http;

        var gateway = new AbpMcpToolGateway(
            new FixedRegistry(MemberTools()),
            new AbpMcpToolVisibility(AllowAll(), EmptyDisabledTools()),
            _invoker,
            accessor,
            root.GetRequiredService<IServiceScopeFactory>());

        await gateway.CallAsync(AbpMcpGatewayToolNames.CallMany,
            Json(new { calls = new[] { new { name = "Member_Get" }, new { name = "Member_Get" }, new { name = "Member_Get" } } }), CancellationToken.None);
        await gateway.CallAsync(AbpMcpGatewayToolNames.Call, Json(new { name = "Member_Get" }), CancellationToken.None);

        var seen = root.GetRequiredService<List<ScopeProbe>>();
        seen.Should().HaveCount(4).And.OnlyHaveUniqueItems("every call gets a scope of its own")
            .And.NotContain(request.ServiceProvider.GetRequiredService<ScopeProbe>(), "not the request's scope");
        http.RequestServices.Should().BeSameAs(request.ServiceProvider, "the request's own scope is restored afterwards");
        _invoker.Calls.Should().BeEmpty("calls resolve the invoker from their own scope");
    }

    [Theory]
    [InlineData("""{"calls":[]}""", "1 to 20 calls")]
    [InlineData("""{"calls":[{"name":"Member_Get"},{"name":"mcp_call_tool"}]}""", "is a gateway tool")]
    [InlineData("""{"calls":[{"name":"Member_Get"}],"mode":"eventually"}""", "'mode' must be")]
    [InlineData("""{"calls":"Member_Get"}""", "'calls' is required")]
    [InlineData("""{"calls":[{"name":"Member_Get"},{"arguments":{}}]}""", "'calls[1].name' is required")]
    public async Task CallTools_RefusesABadBatch_BeforeRunningAnything(string arguments, string message)
    {
        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.CallMany, Parse(arguments), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().StartWith("[VALIDATION_ERROR]").And.Contain(message);
        _invoker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CallTools_RefusesMoreThanTwentyCalls()
    {
        var calls = Enumerable.Range(0, 21).Select(_ => new { name = "Member_Get" }).ToArray();

        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.CallMany, Json(new { calls }), CancellationToken.None);

        outcome.Text.Should().Be("[VALIDATION_ERROR] 'calls' must hold 1 to 20 calls; it holds 21.");
        _invoker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GatewayTool_HonoursTheKillSwitch()
    {
        _disabled.Add(AbpMcpGatewayToolNames.CallMany);

        var outcome = await NewGateway(MemberTools()).CallAsync(AbpMcpGatewayToolNames.CallMany,
            Json(new { calls = new[] { new { name = "Member_Get" } } }), CancellationToken.None);

        outcome.Text.Should().Be("[DISABLED] Tool 'mcp_call_tools' is administratively disabled.");
        _invoker.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Suspend a member. Requires a reason.", "Suspend a member.")]
    [InlineData("Line one\n  continues here. Second sentence.", "Line one continues here.")]
    [InlineData("No terminal period", "No terminal period")]
    [InlineData("Version 1.2 is supported. More.", "Version 1.2 is supported.")]
    public void OneLine_IsTheFirstSentence(string description, string expected) =>
        GatewayResults.OneLine(description).Should().Be(expected);

    [Fact]
    public void OneLine_IsCutAtAWordBoundary_ToAtMost160Characters()
    {
        var line = GatewayResults.OneLine(string.Join(' ', Enumerable.Repeat("word", 80)));

        line.Length.Should().BeLessThanOrEqualTo(GatewayResults.OneLineMaxLength);
        line.Should().EndWith("word…");
    }

    private AbpMcpToolGateway NewGateway(IReadOnlyList<ToolDescriptor> tools)
    {
        var authorizer = Substitute.For<IAbpMcpToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<HttpContext?>())
            .Returns(call => call.Arg<ToolDescriptor>().RequiredPermissions.Count == 0
                ? AbpMcpToolAuthorizationResult.Success()
                : AbpMcpToolAuthorizationResult.Fail("Missing permission"));
        var disabledTools = Substitute.For<IDisabledToolsProvider>();
        disabledTools.GetDisabledToolsAsync(Arg.Any<CancellationToken>()).Returns(_ => _disabled);

        return new AbpMcpToolGateway(
            new FixedRegistry(tools),
            new AbpMcpToolVisibility(authorizer, disabledTools),
            _invoker,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
    }

    private static List<ToolDescriptor> MemberTools() =>
    [
        Tool("Member_Get", "GET"),
        Tool("Member_Suspend", "POST", onDemand: true, description: "Suspend a member with a reason. Suspended members cannot borrow."),
        Tool(Forbidden, "DELETE", onDemand: true, permission: "Library.Members.Delete"),
        Tool(Disabled, "DELETE", onDemand: true),
    ];

    private static ToolDescriptor Tool(string name, string verb, bool onDemand = false, string? permission = null, string? description = null) => new()
    {
        Name = name,
        Description = description ?? $"{name.Replace('_', ' ')} for a member.",
        HttpMethod = verb,
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["reason"] = new JsonObject { ["type"] = "string" } },
        },
        OutputSchema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        ParameterNames = ["reason"],
        RequiredPermissions = permission is null ? [] : [permission],
        Listing = onDemand ? McpToolListing.OnDemand : McpToolListing.Listed,
    };

    private static async Task<JsonNode> CallAsync(AbpMcpToolGateway gateway, string tool, object arguments)
    {
        var outcome = await gateway.CallAsync(tool, Json(arguments), CancellationToken.None);
        outcome.IsError.Should().BeFalse(outcome.Text);
        outcome.StructuredContent.Should().NotBeNull();
        JsonNode.Parse(outcome.Text)!.ToJsonString().Should().Be(outcome.StructuredContent!.Value.GetRawText(),
            "the text block and structuredContent carry the same payload");
        return JsonNode.Parse(outcome.Text)!;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Name(JsonNode? node) => node!["name"]!.GetValue<string>();

    private static IEnumerable<string> Statuses(JsonNode result) =>
        result["results"]!.AsArray().Select(r => r!["status"]!.GetValue<string>());

    private static IAbpMcpToolAuthorizer AllowAll()
    {
        var authorizer = Substitute.For<IAbpMcpToolAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<ToolDescriptor>(), Arg.Any<HttpContext?>()).Returns(AbpMcpToolAuthorizationResult.Success());
        return authorizer;
    }

    private static IDisabledToolsProvider EmptyDisabledTools()
    {
        var provider = Substitute.For<IDisabledToolsProvider>();
        provider.GetDisabledToolsAsync(Arg.Any<CancellationToken>()).Returns(_ => new HashSet<string>(StringComparer.Ordinal));
        return provider;
    }

    private sealed class ScopeProbe;

    private sealed class ProbingInvoker(IHttpContextAccessor accessor, List<ScopeProbe> seen) : IAbpMcpToolInvoker
    {
        public Task<AbpMcpToolCallOutcome> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            seen.Add(accessor.HttpContext!.RequestServices.GetRequiredService<ScopeProbe>());
            return Task.FromResult(AbpMcpToolCallOutcome.Success("{}", null));
        }
    }

    private sealed class RecordingInvoker : IAbpMcpToolInvoker
    {
        /// <summary>A tool name whose call throws past the invoker, as a failed kill-switch read would.</summary>
        public const string Throws = "Member_Throws";

        public Action? OnInvoke { get; set; }

        public Dictionary<string, AbpMcpToolCallOutcome> Outcomes { get; } = new()
        {
            ["Member_Get"] = AbpMcpToolCallOutcome.Success("""{"id":7}""", JsonDocument.Parse("""{"id":7}""").RootElement.Clone()),
            ["Member_Suspend"] = AbpMcpToolCallOutcome.Success("42", null),
            ["Member_Fail"] = AbpMcpToolCallOutcome.Error("LIBRARY:BOOM", "It failed."),
        };

        public List<(string Name, string Arguments)> Calls { get; } = new();

        public Task<AbpMcpToolCallOutcome> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Add((toolName, arguments.GetRawText()));
            OnInvoke?.Invoke();
            return toolName == Throws
                ? throw new InvalidOperationException("The settings store is unavailable.")
                : Task.FromResult(Outcomes[toolName]);
        }
    }

    private sealed class FixedRegistry(IReadOnlyList<ToolDescriptor> tools) : IDynamicMcpToolRegistry
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = tools;

        public void Initialize()
        {
        }

        public void Refresh()
        {
        }

        public bool TryGetByName(string toolName, out ToolDescriptor? descriptor)
        {
            descriptor = Tools.FirstOrDefault(t => t.Name == toolName);
            return descriptor is not null;
        }
    }
}
