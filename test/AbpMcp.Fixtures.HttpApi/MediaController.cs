using AbpMcp.Fixtures.Application;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Content;

namespace AbpMcp.Fixtures.HttpApi;

/// <summary>
/// A hand-written controller for <see cref="IMediaAppService"/>, as ABP solutions write one for multipart
/// uploads or custom routes. ABP's api-definition lists this type, not <see cref="MediaAppService"/>.
/// </summary>
[RemoteService(Name = "FixtureMedia")]
[Area("fixture")]
[Route("api/fixture/media")]
public sealed class MediaController : AbpControllerBase, IMediaAppService
{
    private readonly IMediaAppService _media;

    public MediaController(IMediaAppService media) => _media = media;

    [HttpGet("{id}")]
    public Task<MediaDto> GetAsync(Guid id) => _media.GetAsync(id);

    [HttpPost]
    public Task<MediaDto> UploadAsync(IRemoteStreamContent content) => _media.UploadAsync(content);
}
