using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace AbpMcp.Fixtures.Application;

/// <summary>The fixture *.Application module: its services are tagged through AbpMcp.Abstractions only.</summary>
[DependsOn(typeof(AbpDddApplicationContractsModule))]
public sealed class FixturesApplicationModule : AbpModule
{
}
