using System.Diagnostics;
using System.Reflection;
using AbpMcp.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Users;

namespace AbpMcp.Dispatch;

/// <summary>
/// Records a service-backed tool call in ABP's audit log, as <c>AbpAuditActionFilter</c> does for the
/// parallel REST action. ABP's <c>AuditingInterceptor</c> only covers virtual methods on a class proxy and
/// the MCP endpoint is outside the MVC pipeline, so without this most tool calls would leave no audit trail.
/// </summary>
/// <remarks>
/// When the host runs ABP's auditing middleware the action joins that request's audit log, which the
/// middleware saves. Otherwise a scope is opened and saved here, the way <c>AuditingInterceptor</c> does.
/// </remarks>
internal static class AbpMcpAuditScope
{
    /// <summary>Run <paramref name="invoke"/>, auditing it when ABP's auditing options call for it.</summary>
    public static async Task<T> RunAsync<T>(
        IServiceProvider services,
        ToolDescriptor descriptor,
        object?[] arguments,
        Func<Task<T>> invoke)
    {
        var options = services.GetRequiredService<IOptions<AbpAuditingOptions>>().Value;
        var helper = services.GetRequiredService<IAuditingHelper>();
        var isRead = AbpMcpDispatcher.IsReadAction(descriptor);
        if (descriptor.Method is null || !ShouldAudit(options, helper, descriptor, isRead))
        {
            return await invoke().ConfigureAwait(false);
        }

        var manager = services.GetRequiredService<IAuditingManager>();
        if (manager.Current is { } ambient)
        {
            return await LogActionAsync(helper, ambient.Log, descriptor, arguments, invoke).ConfigureAwait(false);
        }

        var succeeded = false;
        using var handle = manager.BeginScope();
        try
        {
            var result = await LogActionAsync(helper, manager.Current!.Log, descriptor, arguments, invoke).ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally
        {
            if (ShouldWrite(options, services.GetRequiredService<ICurrentUser>(), hasError: !succeeded))
            {
                await handle.SaveAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> LogActionAsync<T>(
        IAuditingHelper helper,
        AuditLogInfo log,
        ToolDescriptor descriptor,
        object?[] arguments,
        Func<Task<T>> invoke)
    {
        var action = helper.CreateAuditLogAction(log, descriptor.ServiceType, descriptor.Method!, NamedArguments(descriptor.Method!, arguments));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await invoke().ConfigureAwait(false);
        }
        catch (Exception ex) when (RecordException(log, ex))
        {
            throw; // Unreachable: RecordException returns false, so the exception is never caught here.
        }
        finally
        {
            action.ExecutionDuration = Convert.ToInt32(stopwatch.Elapsed.TotalMilliseconds);
            log.Actions.Add(action);
        }
    }

    /// <summary>The same gates <c>AbpAuditActionFilter</c> applies before auditing a REST action.</summary>
    private static bool ShouldAudit(AbpAuditingOptions options, IAuditingHelper helper, ToolDescriptor descriptor, bool isRead)
    {
        if (!options.IsEnabled || (isRead && !options.IsEnabledForGetRequests))
        {
            return false;
        }

        if (!options.IsEnabledForIntegrationServices &&
            descriptor.ServiceType is not null &&
            IntegrationServiceAttribute.IsDefinedOrInherited(descriptor.ServiceType))
        {
            return false;
        }

        return helper.ShouldSaveAudit(descriptor.Method, defaultValue: true);
    }

    /// <summary>The save rule <c>AuditingInterceptor</c> applies to a scope it opened itself.</summary>
    private static bool ShouldWrite(AbpAuditingOptions options, ICurrentUser currentUser, bool hasError) =>
        (options.AlwaysLogOnException && hasError) ||
        options.IsEnabledForAnonymousUsers ||
        currentUser.IsAuthenticated;

    /// <summary>Exception filter: records the failure on the audit log and lets it propagate untouched.</summary>
    private static bool RecordException(AuditLogInfo log, Exception ex)
    {
        log.Exceptions.Add(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
        return false;
    }

    private static Dictionary<string, object?> NamedArguments(MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        var named = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            if (parameters[i].ParameterType != typeof(CancellationToken))
            {
                named[parameters[i].Name!] = arguments[i];
            }
        }

        return named;
    }
}
