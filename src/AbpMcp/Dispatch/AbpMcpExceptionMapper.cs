using AbpMcp.Metadata;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Validation;

namespace AbpMcp.Dispatch;

/// <summary>
/// The dispatcher's error mapping table (DESIGN.md Section 1H). Every ABP exception type an app service
/// can raise has a specific translation; anything else becomes <c>INTERNAL</c> with no detail leaked.
/// </summary>
internal static class AbpMcpExceptionMapper
{
    /// <summary>
    /// ABP's message is a generic "arguments are not valid"; the per-member errors are what an agent
    /// needs to correct its call. These are the same validation errors ABP's REST error response returns.
    /// </summary>
    private static string DescribeValidationErrors(AbpValidationException ve)
    {
        if (ve.ValidationErrors.Count == 0)
        {
            return ve.Message;
        }

        var details = ve.ValidationErrors.Select(e => e.MemberNames.Any()
            ? $"{string.Join(", ", e.MemberNames)}: {e.ErrorMessage}"
            : e.ErrorMessage ?? string.Empty);

        return $"{ve.Message} {string.Join("; ", details)}";
    }

    /// <summary>
    /// ABP raises coded authorization failures (e.g. the feature checker's <c>Volo.Feature:010001</c>) with no
    /// message, which would otherwise surface as the CLR default "Exception of type … was thrown.".
    /// </summary>
    private static string DescribeAuthorizationFailure(AbpAuthorizationException ae)
    {
        if (string.IsNullOrEmpty(ae.Code))
        {
            return ae.Message;
        }

        var details = ae.Data.Keys.Cast<object>().Select(key => $"{key}={ae.Data[key]}").ToArray();
        return details.Length == 0
            ? $"Not authorized ({ae.Code})."
            : $"Not authorized ({ae.Code}): {string.Join(", ", details)}.";
    }

    /// <summary>Same wording as ABP's REST error for a missing entity: the entity's short name and id.</summary>
    private static string DescribeNotFound(EntityNotFoundException nfe) =>
        nfe.EntityType is null
            ? "The requested entity was not found."
            : $"There is no {nfe.EntityType.Name} with id = {nfe.Id}.";

    /// <summary>Translate an exception raised while invoking <paramref name="descriptor"/> into the error an agent sees.</summary>
    public static AbpMcpToolException Map(ToolDescriptor descriptor, ILogger logger, Exception ex)
    {
        switch (ex)
        {
            case AbpValidationException ve:
                logger.LogInformation(ex, "Tool {Tool} rejected invalid input", descriptor.Name);
                return new AbpMcpToolException("VALIDATION_ERROR", DescribeValidationErrors(ve), ve);

            case AbpAuthorizationException ae:
                logger.LogWarning(ex, "Tool {Tool} refused unauthorized caller", descriptor.Name);
                return new AbpMcpToolException("FORBIDDEN", DescribeAuthorizationFailure(ae), ae);

            case EntityNotFoundException nfe:
                logger.LogInformation(ex, "Tool {Tool} did not find the requested entity", descriptor.Name);
                return new AbpMcpToolException("NOT_FOUND", DescribeNotFound(nfe), nfe);

            case BusinessException be:
                // UserFriendlyException inherits BusinessException in ABP, so this branch handles both.
                logger.LogInformation(ex, "Tool {Tool} raised business exception {Code}", descriptor.Name, be.Code);
                return new AbpMcpToolException(be.Code ?? "BUSINESS_ERROR", be.Message ?? string.Empty, be);

            case OperationCanceledException oce:
                logger.LogDebug(ex, "Tool {Tool} cancelled", descriptor.Name);
                return new AbpMcpToolException("CANCELLED", "Operation cancelled.", oce);

            default:
                logger.LogError(ex, "Tool {Tool} threw unhandled exception", descriptor.Name);
                return new AbpMcpToolException("INTERNAL", "An internal error occurred.", ex);
        }
    }
}
