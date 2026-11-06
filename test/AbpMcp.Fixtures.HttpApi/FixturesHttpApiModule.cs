using AbpMcp.Fixtures.Application;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Modularity;

namespace AbpMcp.Fixtures.HttpApi;

/// <summary>The fixture *.HttpApi module. Load it as a plug-in to bring in the Application fixture too.</summary>
[DependsOn(typeof(FixturesApplicationModule), typeof(AbpAspNetCoreMvcModule))]
public sealed class FixturesHttpApiModule : AbpModule
{
}
