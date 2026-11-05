using AbpMcp.Metadata;
using FluentAssertions;
using NSubstitute;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Tests;

/// <summary>
/// abp-mcp compiles against ABP 10.1, where the api-definition call is <c>CreateApiModel</c>; ABP 10.3
/// renamed it <c>CreateApiModelAsync</c>, and a host on the newer ABP failed at boot with
/// MissingMethodException. The call is bound to whichever method the host's ABP has. (The integration
/// tests run on the newest ABP in CI, see AbpHostVersion in Directory.Build.props.)
/// </summary>
public class ApiDescriptionModelsTests
{
    private static readonly ApplicationApiDescriptionModelRequestDto Input = new() { IncludeTypes = true };

    [Fact]
    public async Task Bind_UsesCreateApiModelAsync_TheAbp103Shape()
    {
        var model = ApplicationApiDescriptionModel.Create();
        var provider = new AsyncOnlyProvider(model);

        var result = await ApiDescriptionModels.Bind(typeof(IAsyncOnlyContract))(provider, Input);

        result.Should().BeSameAs(model);
        provider.LastInput.Should().BeSameAs(Input);
    }

    [Fact]
    public async Task Bind_UsesCreateApiModel_TheAbp101Shape()
    {
        var model = ApplicationApiDescriptionModel.Create();
        var provider = new SyncOnlyProvider(model);

        var result = await ApiDescriptionModels.Bind(typeof(ISyncOnlyContract))(provider, Input);

        result.Should().BeSameAs(model);
        provider.LastInput.Should().BeSameAs(Input);
    }

    [Fact]
    public async Task Bind_PrefersTheAsyncMethod_WhenBothExist()
    {
        var asyncModel = ApplicationApiDescriptionModel.Create();
        var provider = new BothProvider(asyncModel, ApplicationApiDescriptionModel.Create());

        var result = await ApiDescriptionModels.Bind(typeof(IBothContract))(provider, Input);

        result.Should().BeSameAs(asyncModel);
    }

    [Fact]
    public void Bind_FailsWithAClearMessage_WhenNeitherMethodExists()
    {
        var bind = () => ApiDescriptionModels.Bind(typeof(INeitherContract));

        bind.Should().Throw<MissingMethodException>()
            .WithMessage("*neither CreateApiModelAsync nor CreateApiModel*not supported by abp-mcp*");
    }

    [Fact]
    public async Task ProviderExceptions_SurfaceUnwrapped()
    {
        var call = ApiDescriptionModels.Bind(typeof(ISyncOnlyContract));

        var act = () => call(new ThrowingProvider(), Input);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task CreateAsync_CallsTheHostAbpProvider()
    {
        var model = ApplicationApiDescriptionModel.Create();
        var provider = Substitute.For<IApiDescriptionModelProvider>();
        provider.CreateApiModel(Input).Returns(model);

        var result = await ApiDescriptionModels.CreateAsync(provider, Input);

        result.Should().BeSameAs(model);
    }

    public interface IAsyncOnlyContract
    {
        Task<ApplicationApiDescriptionModel> CreateApiModelAsync(ApplicationApiDescriptionModelRequestDto input);
    }

    public interface ISyncOnlyContract
    {
        ApplicationApiDescriptionModel CreateApiModel(ApplicationApiDescriptionModelRequestDto input);
    }

    public interface IBothContract
    {
        Task<ApplicationApiDescriptionModel> CreateApiModelAsync(ApplicationApiDescriptionModelRequestDto input);

        ApplicationApiDescriptionModel CreateApiModel(ApplicationApiDescriptionModelRequestDto input);
    }

    public interface INeitherContract
    {
        ApplicationApiDescriptionModel Describe(ApplicationApiDescriptionModelRequestDto input);
    }

    private sealed class AsyncOnlyProvider(ApplicationApiDescriptionModel model) : IAsyncOnlyContract
    {
        public ApplicationApiDescriptionModelRequestDto? LastInput { get; private set; }

        public Task<ApplicationApiDescriptionModel> CreateApiModelAsync(ApplicationApiDescriptionModelRequestDto input)
        {
            LastInput = input;
            return Task.FromResult(model);
        }
    }

    private sealed class SyncOnlyProvider(ApplicationApiDescriptionModel model) : ISyncOnlyContract
    {
        public ApplicationApiDescriptionModelRequestDto? LastInput { get; private set; }

        public ApplicationApiDescriptionModel CreateApiModel(ApplicationApiDescriptionModelRequestDto input)
        {
            LastInput = input;
            return model;
        }
    }

    private sealed class BothProvider(ApplicationApiDescriptionModel asyncModel, ApplicationApiDescriptionModel syncModel)
        : IBothContract
    {
        public Task<ApplicationApiDescriptionModel> CreateApiModelAsync(ApplicationApiDescriptionModelRequestDto input) =>
            Task.FromResult(asyncModel);

        public ApplicationApiDescriptionModel CreateApiModel(ApplicationApiDescriptionModelRequestDto input) => syncModel;
    }

    private sealed class ThrowingProvider : ISyncOnlyContract
    {
        public ApplicationApiDescriptionModel CreateApiModel(ApplicationApiDescriptionModelRequestDto input) =>
            throw new InvalidOperationException("boom");
    }
}
