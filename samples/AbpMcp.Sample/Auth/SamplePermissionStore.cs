using Microsoft.Extensions.Options;
using Volo.Abp.Authorization.Permissions;

namespace AbpMcp.Sample.Auth;

/// <summary>
/// The sample's permission store: user grants from <see cref="SampleAuthOptions.Grants"/>. A real host keeps
/// them in ABP's permission management module; the checker reads them through this same interface.
/// </summary>
public sealed class SamplePermissionStore : IPermissionStore
{
    private readonly IOptions<SampleAuthOptions> _options;

    /// <summary>Creates the store.</summary>
    public SamplePermissionStore(IOptions<SampleAuthOptions> options) => _options = options;

    /// <inheritdoc />
    public Task<bool> IsGrantedAsync(string name, string? providerName, string? providerKey) =>
        Task.FromResult(IsGranted(name, providerName, providerKey));

    /// <inheritdoc />
    public Task<MultiplePermissionGrantResult> IsGrantedAsync(string[] names, string? providerName, string? providerKey)
    {
        var result = new MultiplePermissionGrantResult();
        foreach (var name in names)
        {
            result.Result[name] = IsGranted(name, providerName, providerKey)
                ? PermissionGrantResult.Granted
                : PermissionGrantResult.Undefined;
        }

        return Task.FromResult(result);
    }

    private bool IsGranted(string name, string? providerName, string? providerKey) =>
        providerName == UserPermissionValueProvider.ProviderName &&
        providerKey is not null &&
        _options.Value.GrantsFor(providerKey).Contains(name);
}
