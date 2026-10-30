using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AbpMcp.Addins;
using AbpMcp.Authorization;
using AbpMcp.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Http;
using Volo.Abp.Uow;
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
    private readonly IAbpMcpToolAuthorizer _authorizer;
    private readonly IOptionsMonitor<AbpMcpOptions> _options;
    private readonly ILogger<AbpMcpDispatcher> _logger;

    public AbpMcpDispatcher(
        IHttpContextAccessor httpContextAccessor,
        IAbpMcpToolAuthorizer authorizer,
        IOptionsMonitor<AbpMcpOptions> options,
        ILogger<AbpMcpDispatcher> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizer = authorizer;
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

                using var uow = BeginUnitOfWork(httpContext.RequestServices, descriptor);
                var handled = await descriptor.Handler(invocation, cancellationToken).ConfigureAwait(false);
                await CompleteAsync(uow, cancellationToken).ConfigureAwait(false);
                return handled;
            }
            catch (Exception ex) when (ex is not AbpMcpToolException)
            {
                throw AbpMcpExceptionMapper.Map(descriptor, _logger, ex);
            }
        }

        if (descriptor.ServiceType is null || descriptor.Method is null)
        {
            throw new AbpMcpToolException(
                "INTERNAL",
                $"Tool '{descriptor.Name}' has neither a dynamic handler nor a service method to invoke.");
        }

        try
        {
            // Inside the try so a resolution or binding failure goes through the mapping table too.
            var service = httpContext.RequestServices.GetRequiredService(descriptor.ServiceType);
            var invocationArgs = MapArguments(descriptor, arguments, cancellationToken);

            // The concrete service is invoked by reflection, so ABP's ValidationInterceptor does not
            // run for sealed or non-virtual app-service methods. Validate explicitly with the same
            // ABP validator the interceptor uses (DataAnnotations, IValidatableObject, contributors),
            // so a tools/call cannot pass input the parallel HTTP endpoint would reject.
            var validator = httpContext.RequestServices.GetRequiredService<IMethodInvocationValidator>();
            // ABP 9.2 annotates parameterValues as object[] (10.x as object?[]); null entries are what it validates.
            await validator.ValidateAsync(new MethodInvocationValidationContext(service, descriptor.Method, (object[])invocationArgs))
                .ConfigureAwait(false);

            // Disposed without CompleteAsync when the method throws, which discards its writes.
            using var uow = BeginUnitOfWork(httpContext.RequestServices, descriptor);

            var raw = descriptor.Method.Invoke(service, invocationArgs);
            var result = await UnwrapAsync(raw).ConfigureAwait(false);

            // Serialize inside the unit of work so lazily loaded members still resolve.
            var serialized = SerializeResult(result);
            await CompleteAsync(uow, cancellationToken).ConfigureAwait(false);
            return serialized;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw AbpMcpExceptionMapper.Map(descriptor, _logger, tie.InnerException);
        }
        catch (Exception ex) when (ex is not AbpMcpToolException)
        {
            throw AbpMcpExceptionMapper.Map(descriptor, _logger, ex);
        }
    }

    /// <summary>
    /// Open the unit of work ABP's MVC UoW filter opens for the parallel REST call. The MCP endpoint
    /// is outside the MVC pipeline, and the concrete service is invoked by reflection, so without
    /// this each repository call commits on its own and a tool that fails midway leaves partial writes.
    /// Joins an already-active unit of work when there is one. Honors <c>[UnitOfWork]</c> on the
    /// method (<c>IsDisabled</c>, <c>IsTransactional</c>, isolation level, timeout) and the host's
    /// <see cref="AbpUnitOfWorkDefaultOptions.TransactionBehavior"/>.
    /// </summary>
    private static IUnitOfWork? BeginUnitOfWork(IServiceProvider services, ToolDescriptor descriptor)
    {
        var attribute = descriptor.Method is null ? null : UnitOfWorkHelper.GetUnitOfWorkAttributeOrNull(descriptor.Method);
        if (attribute?.IsDisabled == true)
        {
            return null;
        }

        var defaults = services.GetRequiredService<IOptions<AbpUnitOfWorkDefaultOptions>>().Value;
        return services.GetRequiredService<IUnitOfWorkManager>().Begin(new AbpUnitOfWorkOptions
        {
            IsTransactional = attribute?.IsTransactional ?? defaults.CalculateIsTransactional(autoValue: !IsReadAction(descriptor)),
            IsolationLevel = attribute?.IsolationLevel,
            Timeout = attribute?.Timeout,
        });
    }

    /// <summary>
    /// ABP's UoW filter makes a <c>GET</c> action non-transactional and every other verb transactional.
    /// Every tools/call arrives as a POST, so decide by the verb of the parallel REST action instead:
    /// the api-definition's verb when known, else ABP's method-name convention (<c>Get*</c> is GET).
    /// A dynamic tool with no verb is treated as a write.
    /// </summary>
    internal static bool IsReadAction(ToolDescriptor descriptor)
    {
        var verb = descriptor.HttpMethod
            ?? (descriptor.Method is null ? null : HttpMethodHelper.GetConventionalVerbForMethodName(descriptor.Method.Name));
        return string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase);
    }

    private static Task CompleteAsync(IUnitOfWork? uow, CancellationToken cancellationToken) =>
        uow is null ? Task.CompletedTask : uow.CompleteAsync(cancellationToken);

    private async Task EnsureAuthorizedAsync(HttpContext context, ToolDescriptor descriptor)
    {
        var result = await _authorizer.AuthorizeAsync(descriptor, context).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new AbpMcpToolException("FORBIDDEN", result.FailureReason ?? "Not authorized.");
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
                try
                {
                    values[i] = element.Deserialize(parameter.ParameterType, JsonOptions);
                }
                catch (JsonException jex)
                {
                    // The path is relative to the agent's own argument, so it is safe and actionable to echo.
                    throw new AbpMcpToolException(
                        "VALIDATION_ERROR",
                        $"Parameter '{parameter.Name}' does not match its schema (at '{jex.Path ?? "$"}').",
                        jex);
                }

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
}
