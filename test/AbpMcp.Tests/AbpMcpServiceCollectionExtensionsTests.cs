using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpMcp.Tests;

/// <summary>
/// Locks in the convenience-extension contract: <c>AddAbpMcpAssembly</c> must populate
/// both option sets (ABP's ConventionalControllers AND abp-mcp's ExposedAssemblies)
/// so a future refactor can't silently drop one half and reintroduce the v0.1
/// "I configured one but not the other and got zero tools" trap.
/// </summary>
public class AbpMcpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAbpMcpAssembly_RegistersAssemblyWithMcpOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var asm = typeof(AbpMcpServiceCollectionExtensionsTests).Assembly;

        services.AddAbpMcpAssembly(asm);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<AbpMcpOptions>>().Value;

        opts.ExposedAssemblies.Should().ContainSingle()
            .Which.Assembly.Should().BeSameAs(asm);
    }

    [Fact]
    public void AddAbpMcpAssembly_RegistersAssemblyWithConventionalControllers()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var asm = typeof(AbpMcpServiceCollectionExtensionsTests).Assembly;

        services.AddAbpMcpAssembly(asm);
        using var sp = services.BuildServiceProvider();

        var mvcOpts = sp.GetRequiredService<IOptions<AbpAspNetCoreMvcOptions>>().Value;

        mvcOpts.ConventionalControllers.ConventionalControllerSettings
            .Any(s => s.Assembly == asm)
            .Should().BeTrue(because: "ABP's api-definition provider only surfaces app services from registered ConventionalControllers");
    }

    [Fact]
    public void AddAbpMcpAssembly_NullAssemblyThrows()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAbpMcpAssembly(assembly: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddAbpMcpAssembly_NullServicesThrows()
    {
        IServiceCollection? services = null;
        var act = () => services!.AddAbpMcpAssembly(typeof(AbpMcpServiceCollectionExtensionsTests).Assembly);
        act.Should().Throw<ArgumentNullException>();
    }
}
