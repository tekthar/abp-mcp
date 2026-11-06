# Services served by hand-written controllers

**Status:** not supported, by design ([#101](https://github.com/tekthar/abp-mcp/issues/101)). `/mcp/_explain` reports
such services, and this page gives the pattern that exposes their JSON-friendly operations.

## The pattern this is about

ABP's usual answer for an endpoint that needs MVC (a multipart upload with `IRemoteStreamContent` or `IFormFile`,
file streaming, a custom route, a redirect) is:

1. mark the app service `[RemoteService(IsEnabled = false)]`, so ABP does not generate a conventional controller for it;
2. write a controller in `*.HttpApi` that implements the service interface and delegates to it.

```csharp
// MyApp.Application
[McpTool] // has no effect, see below
[RemoteService(IsEnabled = false)]
public class MediaAppService : ApplicationService, IMediaAppService
{
    public Task<MediaDto> GetAsync(Guid id) => /* ... */;
    public Task<MediaDto> UploadAsync(IRemoteStreamContent content) => /* ... */;
}

// MyApp.HttpApi
[RemoteService(Name = "Media")]
[Route("api/media")]
public class MediaController : AbpControllerBase, IMediaAppService
{
    // [HttpGet("{id}")] GetAsync, [HttpPost] UploadAsync, each delegating to IMediaAppService
}
```

## Why `[McpTool]` does nothing here

abp-mcp builds its tools from ABP's api-definition (`/api/abp/api-definition`), the same metadata ABP's proxy
generators read. That is design rule 1 in [`DESIGN.md`](../DESIGN.md), and it is what keeps the MCP surface identical to
your REST surface: the same services, the same parameters, the same permissions.

For the pattern above, the api-definition describes `MediaController`, not `MediaAppService`. The service is not in the
metadata, so abp-mcp never sees its `[McpTool]`. Tagging the controller instead does not help either: abp-mcp invokes
app services through the dispatcher, not MVC actions (model binding, `IActionResult`, form files), and the controller's
`*.HttpApi` assembly is not an app-service assembly.

Two other designs were considered and set aside for now:

- **Map a controller back to its service** (invoke the service an api-definition controller implements). It keeps the
  api-definition as the source of truth, but needs the dispatcher to resolve and invoke through a different type than the
  one described, and to reject non-JSON parameters per action. That is a larger change than the use case justifies today.
- **Enumerate `[McpTool]` app services from the module graph** instead of the api-definition. It breaks design rule 1.

## What `/mcp/_explain` says

`_explain` looks for `[McpTool]` services in the exposed assemblies that the api-definition does not describe, and lists
each of their methods as excluded, with the reason. For the example above:

```json
{
  "service": "MyApp.Media.MediaAppService",
  "method": "UploadAsync",
  "included": false,
  "reason": "Excluded: MediaAppService is not in ABP's api-definition: it is [RemoteService(IsEnabled = false)], so it is served by a hand-written controller or not remoted at all. It is served by the hand-written controller MyApp.Media.MediaController. abp-mcp only exposes app services the api-definition describes, and hand-written controllers are not supported. Wrap the JSON-friendly operations in a thin, remoted [McpTool] app service that delegates to this one. See docs/hand-written-controllers.md."
}
```

The controller is named when an api-definition controller implements one of the service's interfaces. A `[McpTool]`
service that is missing for another reason (it does not implement `IApplicationService`, say) gets a reason that lists
what to check. These rows only appear when abp-mcp has exposed assemblies to look in, which is the default with
auto-discovery; with `AutoDiscoverAssemblies = false` and nothing registered there is no bounded set to scan.

## The wrapper pattern

Add a thin, **remoted** app service that exposes the JSON-friendly operations and delegates to the real one. ABP gives
it a conventional controller, so it is in the api-definition and its `[McpTool]` takes effect:

```csharp
// MyApp.Application
[McpTool]
public class MediaToolsAppService : ApplicationService
{
    private readonly IMediaAppService _media;

    public MediaToolsAppService(IMediaAppService media) => _media = media;

    [Authorize(MyAppPermissions.Media.Default)]
    public Task<MediaDto> GetAsync(Guid id) => _media.GetAsync(id);

    // No UploadAsync: a stream or a form file has no JSON shape an agent can send.
}
```

The tool is `MediaTools_Get`. Things to keep in mind:

- **Do not mark the wrapper `[RemoteService(IsEnabled = false)]`.** It must be remoted, or it is invisible for the same
  reason as the service it wraps.
- **Put the permissions on the wrapper.** abp-mcp checks the `[Authorize]` attributes of the method it invokes before it
  lists or calls the tool. Repeat the wrapped method's permissions, or the tool's visibility will not match the REST
  endpoint's.
- **The wrapper is also a REST endpoint** (`/api/app/media-tools/...`). If you would rather not publish it, give it its
  own `[RemoteService(Name = "...")]` group and leave that group out of your proxies; abp-mcp still needs ABP to remote it.
- **Leave out what JSON cannot carry**: `IRemoteStreamContent`, `IFormFile`, raw streams, redirects. For an upload, an
  agent-friendly alternative is an operation that takes a URL or base64 content and calls the same domain logic.
