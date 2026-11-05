using System.Security.Claims;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace AbpMcp.Authorization;

/// <summary>
/// Default <see cref="IAbpMcpToolAuthorizer"/>. Enforces the named permissions first (so a refusal
/// names the missing permission, as before), then the combined <c>[Authorize]</c> policy built by
/// ASP.NET Core's own <c>AuthorizationPolicy.CombineAsync(provider, authorizeData)</c> — the same combination the MVC authorization filter applies to the parallel HTTP endpoint, so
/// roles, schemes and authenticated-only requirements are no longer dropped. A permission or policy the host
/// does not define refuses the tool, for every caller, instead of failing the whole request (#84).
/// </summary>
internal sealed class AbpMcpToolAuthorizer : IAbpMcpToolAuthorizer
{
    private readonly IAuthorizationService _authorization;
    private readonly IAuthorizationPolicyProvider _policyProvider;
    private readonly UndefinedPermissionLog _undefinedPermissions;

    public AbpMcpToolAuthorizer(
        IAuthorizationService authorization,
        IAuthorizationPolicyProvider policyProvider,
        UndefinedPermissionLog undefinedPermissions)
    {
        _authorization = authorization;
        _policyProvider = policyProvider;
        _undefinedPermissions = undefinedPermissions;
    }

    public async Task<AbpMcpToolAuthorizationResult> AuthorizeAsync(ToolDescriptor descriptor, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.RequiredPermissions.Count == 0 && descriptor.AuthorizeData.Count == 0)
        {
            return AbpMcpToolAuthorizationResult.Success();
        }

        var user = httpContext?.User;
        if (httpContext is null || user is null)
        {
            return AbpMcpToolAuthorizationResult.Fail("An authenticated caller is required.");
        }

        // ASP.NET Core throws "No policy found" for a name nothing defines, which failed tools/list and _discover
        // as a whole. Refuse just this tool instead. Fail closed: an undefined name never grants anything.
        foreach (var name in PolicyNames(descriptor))
        {
            if (await _policyProvider.GetPolicyAsync(name).ConfigureAwait(false) is null)
            {
                _undefinedPermissions.Report(descriptor.Name, name);
                return AbpMcpToolAuthorizationResult.Fail($"Missing permission: {name} (it is not defined on this server).");
            }
        }

        foreach (var permission in descriptor.RequiredPermissions)
        {
            var result = await _authorization.AuthorizeAsync(user, permission).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return AbpMcpToolAuthorizationResult.Fail($"Missing permission: {permission}");
            }
        }

        if (descriptor.AuthorizeData.Count == 0)
        {
            return AbpMcpToolAuthorizationResult.Success();
        }

        var policy = await AuthorizationPolicy.CombineAsync(_policyProvider, descriptor.AuthorizeData).ConfigureAwait(false);
        if (policy is null)
        {
            return AbpMcpToolAuthorizationResult.Success();
        }

        if (policy.AuthenticationSchemes.Count > 0)
        {
            user = await AuthenticateSchemesAsync(httpContext, policy.AuthenticationSchemes).ConfigureAwait(false);
        }

        var combined = await _authorization.AuthorizeAsync(user, policy).ConfigureAwait(false);
        return combined.Succeeded
            ? AbpMcpToolAuthorizationResult.Success()
            : AbpMcpToolAuthorizationResult.Fail(DescribeRequirements(descriptor.AuthorizeData));
    }

    /// <summary>
    /// Every permission and policy name <paramref name="descriptor"/> requires. Policies normally appear among the
    /// permissions too, but an add-in can attach <c>[Authorize]</c> data on its own.
    /// </summary>
    internal static IEnumerable<string> PolicyNames(ToolDescriptor descriptor) => descriptor.RequiredPermissions
        .Concat(descriptor.AuthorizeData.Select(data => data.Policy).OfType<string>().Where(policy => policy.Length > 0))
        .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Policies that name authentication schemes are evaluated against the principals those schemes
    /// produce, not the ambient default user — the same rule ASP.NET Core's policy evaluator applies.
    /// </summary>
    private static async Task<ClaimsPrincipal> AuthenticateSchemesAsync(HttpContext httpContext, IReadOnlyList<string> schemes)
    {
        var identities = new List<ClaimsIdentity>();
        foreach (var scheme in schemes)
        {
            var result = await httpContext.AuthenticateAsync(scheme).ConfigureAwait(false);
            if (result.Succeeded && result.Principal is not null)
            {
                identities.AddRange(result.Principal.Identities);
            }
        }

        return identities.Count > 0 ? new ClaimsPrincipal(identities) : new ClaimsPrincipal(new ClaimsIdentity());
    }

    private static string DescribeRequirements(IReadOnlyList<IAuthorizeData> authorizeData)
    {
        var parts = new List<string>();
        foreach (var data in authorizeData)
        {
            if (!string.IsNullOrEmpty(data.Roles))
            {
                parts.Add($"role(s) {data.Roles}");
            }

            if (!string.IsNullOrEmpty(data.AuthenticationSchemes))
            {
                parts.Add($"authentication scheme(s) {data.AuthenticationSchemes}");
            }
        }

        return parts.Count == 0
            ? "An authenticated caller is required."
            : "Not authorized: requires " + string.Join(" and ", parts.Distinct(StringComparer.Ordinal)) + ".";
    }
}
