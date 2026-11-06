using AbpMcp.Attributes;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Fixtures.Application;

/// <summary>A stored media file.</summary>
public sealed class MediaDto
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long Length { get; set; }
}

public interface IMediaAppService : IApplicationService
{
    Task<MediaDto> GetAsync(Guid id);

    Task<MediaDto> UploadAsync(IRemoteStreamContent content);
}

/// <summary>
/// ABP's pattern for an endpoint that needs MVC (#101): the app service is not remoted and a hand-written
/// controller (<c>AbpMcp.Fixtures.HttpApi.MediaController</c>) serves it. ABP's api-definition then describes
/// the controller, not this type, so its <c>[McpTool]</c> cannot take effect; <c>/mcp/_explain</c> must say so.
/// </summary>
[McpTool]
[RemoteService(IsEnabled = false)]
public sealed class MediaAppService : IMediaAppService, ITransientDependency
{
    public Task<MediaDto> GetAsync(Guid id) =>
        Task.FromResult(new MediaDto { Id = id, FileName = $"cover-{id:N}.png", Length = 2048 });

    public async Task<MediaDto> UploadAsync(IRemoteStreamContent content)
    {
        await using var stream = content.GetStream();
        return new MediaDto { Id = Guid.NewGuid(), FileName = content.FileName ?? "upload.bin", Length = stream.Length };
    }
}

/// <summary>
/// The recommended workaround for <see cref="MediaAppService"/> (docs/hand-written-controllers.md): a thin,
/// remoted <c>[McpTool]</c> app service that wraps only the JSON-friendly operations and delegates.
/// </summary>
[McpTool]
public sealed class MediaToolsAppService : IApplicationService, ITransientDependency
{
    private readonly IMediaAppService _media;

    public MediaToolsAppService(IMediaAppService media) => _media = media;

    public Task<MediaDto> GetAsync(Guid id) => _media.GetAsync(id);
}
