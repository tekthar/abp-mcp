using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AbpMcp.Addins;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Validation;

namespace AbpMcp.Dispatch;

/// <summary>
/// Default dispatcher. Implements the permission check + invoke + error-map flow
/// described in DESIGN.md (Section 1H Error mapping table).
/// </summary>
internal sealed class AbpMcpDispatcher : IAbpMcpDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        // The schema advertises enums by their string names (JsonSchemaMapper.MapEnum). Register the
        // string-enum converter so those names actually deserialize — otherwise the Web default would
        // demand the underlying integer and every enum-bearing tool would reject its own schema. The
        // converter still accepts integers on read (allowIntegerValues defaults true) and writes names,
        // so results render the same names the schema promised.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<AbpMcpOptions> _options;
    private readonly ILogger<AbpMcpDispatcher> _logger;

    public AbpMcpDispatcher(
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationService authorization,
        IOptionsMonitor<AbpMcpOptions> options,
        ILogger<AbpMcpDispatcher> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorization = authorization;
        _options = options;
        _logger = logger;
    }

    public async Task<JsonElement> InvokeAsync(
        ToolDescriptor descriptor,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("abp-mcp requires an active HttpContext for every tool call.");

        // Defense in depth: the handler wiring already filters disabled tools out of tools/list,
        // but a stale agent could still target one by name. Re-check at the dispatcher boundary.
        if (_options.CurrentValue.DisabledTools.Contains(descriptor.Name))
        {
            throw new AbpMcpToolException("DISABLED", $"Tool '{descriptor.Name}' is administratively disabled.");
        }

        await EnsureAuthorizedAsync(httpContext, descriptor).ConfigureAwait(false);

        // Dynamic tool contributed by an add-in: run its handler instead of reflecting a service
        // method. The disabled/authorization checks above apply equally to dynamic tools.
        if (descriptor.Handler is not null)
        {
            try
            {
                var invocation = new AbpMcpToolInvocation
                {
                    Descriptor = descriptor,
                    Arguments = arguments,
                    Services = httpContext.RequestServices,
                    User = httpContext.User,
                };

                return await descriptor.Handler(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not AbpMcpToolException)
            {
                throw MapException(descriptor, ex);
            }
        }

        if (descriptor.ServiceType is null || descriptor.Method is null)
        {
            throw new AbpMcpToolException(
                "INTERNAL",
                $"Tool '{descriptor.Name}' has neither a dynamic handler nor a service method to invoke.");
        }

        var service = httpContext.RequestServices.GetRequiredService(descriptor.ServiceType);
        var invocationArgs = MapArguments(descriptor, arguments, cancellationToken);

        try
        {
            var raw = descriptor.Method.Invoke(service, invocationArgs);
            var result = await UnwrapAsync(raw).ConfigureAwait(false);
            return SerializeResult(result);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw MapException(descriptor, tie.InnerException);
        }
        catch (Exception ex) when (ex is not AbpMcpToolException)
        {
            throw MapException(descriptor, ex);
        }
    }

    private async Task EnsureAuthorizedAsync(HttpContext context, ToolDescriptor descriptor)
    {
        foreach (var permission in descriptor.RequiredPermissions)
        {
            var result = await _authorization.AuthorizeAsync(context.User, permission).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new AbpMcpToolException("FORBIDDEN", $"Missing permission: {permission}");
            }
        }
    }

    private static object?[] MapArguments(
        ToolDescriptor descriptor,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parameters = descriptor.Method!.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[i] = cancellationToken;
                continue;
            }

            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty(parameter.Name!, out var element))
            {
                values[i] = element.Deserialize(parameter.ParameterType, JsonOptions);
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
                continue;
            }

            // Required parameter missing: surface a clear validation error to the agent.
            throw new AbpMcpToolException(
                "VALIDATION_ERROR",
                $"Missing required parameter '{parameter.Name}'.");
        }

        return values;
    }

    private static async Task<object?> UnwrapAsync(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                var resultProperty = task.GetType().GetProperty("Result");
                return resultProperty?.GetValue(task);
            default:
                return raw;
        }
    }

    private static JsonElement SerializeResult(object? result)
    {
        if (result is null)
        {
            using var empty = JsonDocument.Parse("null");
            return empty.RootElement.Clone();
        }

        var json = JsonSerializer.SerializeToElement(result, result.GetType(), JsonOptions);
        return json;
    }

    private AbpMcpToolException MapException(ToolDescriptor descriptor, Exception ex)
    {
        switch (ex)
        {
            case AbpValidationException ve:
                _logger.LogInformation(ex, "Tool {Tool} rejected invalid input", descriptor.Name);
                return new AbpMcpToolException("VALIDATION_ERROR", ve.Message, ve);

            case AbpAuthorizationException ae:
                _logger.LogWarning(ex, "Tool {Tool} refused unauthorized caller", descriptor.Name);
                return new AbpMcpToolException("FORBIDDEN", ae.Message, ae);

            case BusinessException be:
                // UserFriendlyException inherits BusinessException in ABP, so this branch handles both.
                _logger.LogInformation(ex, "Tool {Tool} raised business exception {Code}", descriptor.Name, be.Code);
                return new AbpMcpToolException(be.Code ?? "BUSINESS_ERROR", be.Message ?? string.Empty, be);

            case OperationCanceledException oce:
                _logger.LogDebug(ex, "Tool {Tool} cancelled", descriptor.Name);
                return new AbpMcpToolException("CANCELLED", "Operation cancelled.", oce);

            default:
                _logger.LogError(ex, "Tool {Tool} threw unhandled exception", descriptor.Name);
                return new AbpMcpToolException("INTERNAL", "An internal error occurred.", ex);
        }
    }
}
