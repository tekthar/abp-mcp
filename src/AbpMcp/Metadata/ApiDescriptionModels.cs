using System.Reflection;
using Volo.Abp.Http.Modeling;

namespace AbpMcp.Metadata;

/// <summary>
/// Calls ABP's <see cref="IApiDescriptionModelProvider"/> on whichever ABP the host runs.
/// </summary>
/// <remarks>
/// ABP 10.3 replaced <c>CreateApiModel(ApplicationApiDescriptionModelRequestDto)</c> with
/// <c>CreateApiModelAsync(ApplicationApiDescriptionModelRequestDto)</c>. abp-mcp compiles against its ABP
/// floor (10.1), so a direct call binds to the old method, and a host on ABP 10.3 or later failed at boot
/// with <see cref="MissingMethodException"/>. The method is therefore looked up on the interface the host
/// actually loaded, once, preferring the async one.
/// </remarks>
internal static class ApiDescriptionModels
{
    private const string AsyncMethodName = "CreateApiModelAsync";
    private const string SyncMethodName = "CreateApiModel";

    private static readonly Lazy<Func<object, ApplicationApiDescriptionModelRequestDto, Task<ApplicationApiDescriptionModel>>> HostCall =
        new(() => Bind(typeof(IApiDescriptionModelProvider)));

    /// <summary>Builds ABP's api-definition model through the host ABP's own method.</summary>
    public static Task<ApplicationApiDescriptionModel> CreateAsync(
        IApiDescriptionModelProvider provider,
        ApplicationApiDescriptionModelRequestDto input)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(input);

        return HostCall.Value(provider, input);
    }

    /// <summary>
    /// Binds to <c>CreateApiModelAsync</c> on <paramref name="contract"/> when it has one, else to
    /// <c>CreateApiModel</c>. Exceptions thrown by the provider surface unwrapped.
    /// </summary>
    internal static Func<object, ApplicationApiDescriptionModelRequestDto, Task<ApplicationApiDescriptionModel>> Bind(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        Type[] parameters = [typeof(ApplicationApiDescriptionModelRequestDto)];

        var asyncMethod = contract.GetMethod(AsyncMethodName, parameters);
        if (asyncMethod is not null && asyncMethod.ReturnType == typeof(Task<ApplicationApiDescriptionModel>))
        {
            return (provider, input) => (Task<ApplicationApiDescriptionModel>)Invoke(asyncMethod, provider, input);
        }

        var syncMethod = contract.GetMethod(SyncMethodName, parameters);
        if (syncMethod is not null && syncMethod.ReturnType == typeof(ApplicationApiDescriptionModel))
        {
            return (provider, input) => Task.FromResult((ApplicationApiDescriptionModel)Invoke(syncMethod, provider, input));
        }

        throw new MissingMethodException(
            $"{contract.FullName} has neither {AsyncMethodName} nor {SyncMethodName} taking " +
            $"{nameof(ApplicationApiDescriptionModelRequestDto)}; this ABP version is not supported by abp-mcp.");
    }

    private static object Invoke(MethodInfo method, object provider, ApplicationApiDescriptionModelRequestDto input)
    {
        return method.Invoke(provider, BindingFlags.DoNotWrapExceptions, binder: null, [input], culture: null)
               ?? throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} returned null.");
    }
}
