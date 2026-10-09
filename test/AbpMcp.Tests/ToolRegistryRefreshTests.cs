using System.Text.Json.Nodes;
using AbpMcp.Addins;
using AbpMcp.Metadata;
using AbpMcp.Notifications;
using AbpMcp.Registration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AbpMcp.Tests;

public class ToolRegistryRefreshTests
{
    [Fact]
    public void Refresh_RebuildsToolSetFromReader()
    {
        var reader = new SwitchingReader();
        var registry = NewRegistry(reader);

        registry.Initialize();
        registry.Tools.Select(t => t.Name).Should().ContainSingle().Which.Should().Be("Tool_A");

        // The underlying source changed; Refresh must pick it up and swap atomically.
        registry.Refresh();

        registry.Tools.Select(t => t.Name).Should().ContainSingle().Which.Should().Be("Tool_B");
        registry.TryGetByName("Tool_B", out _).Should().BeTrue();
        registry.TryGetByName("Tool_A", out _).Should().BeFalse();
    }

    [Fact]
    public void Refresh_DoesNotThrow_WhenRebuildYieldsZeroTools_EvenIfRequireAtLeastOneTool()
    {
        var reader = new SwitchingReader(secondIsEmpty: true);
        var registry = NewRegistry(reader, requireAtLeastOne: true);

        registry.Initialize();              // first build has a tool, so the startup guard is satisfied
        var act = () => registry.Refresh(); // runtime rebuild must never throw the server down

        act.Should().NotThrow();
        registry.Tools.Should().BeEmpty();
    }

    private static DynamicMcpToolRegistry NewRegistry(IApiDefinitionReader reader, bool requireAtLeastOne = false)
    {
        var options = Options.Create(new AbpMcpOptions { RequireAtLeastOneTool = requireAtLeastOne });
        return new DynamicMcpToolRegistry(
            reader,
            Array.Empty<IAbpMcpAddin>(),
            new NullServiceProvider(),
            options,
            NullLogger<DynamicMcpToolRegistry>.Instance);
    }

    private sealed class SwitchingReader : IApiDefinitionReader
    {
        private readonly bool _secondIsEmpty;
        private int _calls;

        public SwitchingReader(bool secondIsEmpty = false) => _secondIsEmpty = secondIsEmpty;

        public IReadOnlyList<ToolDescriptor> Read()
        {
            _calls++;
            if (_calls == 1)
            {
                return new[] { Tool("Tool_A") };
            }

            return _secondIsEmpty ? Array.Empty<ToolDescriptor>() : new[] { Tool("Tool_B") };
        }

        private static ToolDescriptor Tool(string name) => new()
        {
            Name = name,
            Description = "d",
            InputSchema = new JsonObject { ["type"] = "object" },
            ParameterNames = Array.Empty<string>(),
            RequiredPermissions = Array.Empty<string>(),
        };
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

public class ToolListChangedNotifierTests
{
    [Fact]
    public async Task Notify_RefreshesRegistry_AndToleratesNoSessions()
    {
        var registry = Substitute.For<IDynamicMcpToolRegistry>();
        var notifier = new AbpMcpToolListChangedNotifier(
            registry,
            new McpSessionRegistry(),
            NullLogger<AbpMcpToolListChangedNotifier>.Instance);

        await notifier.NotifyToolListChangedAsync();

        registry.Received(1).Refresh();
    }
}
