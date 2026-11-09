using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Management;

namespace TerraBase.Fast.Backup;

// Holds one IManagementService.CopyDatabaseAsync call — the restore copy — until released, forwarding every
// other member to the decorated service through DispatchProxy so the gate survives interface growth.
internal sealed class CopyGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public void Decorate(IServiceCollection services)
    {
        var registered = services.Last(candidate => candidate.ServiceType == typeof(IManagementService));
        services.Replace(ServiceDescriptor.Describe(
            typeof(IManagementService),
            provider => Gated((IManagementService)registered.ImplementationFactory!.Invoke(provider), this),
            registered.Lifetime));
    }

    private static IManagementService Gated(IManagementService inner, CopyGate gate)
    {
        var proxy = DispatchProxy.Create<IManagementService, HoldManagement>();
        ((HoldManagement)(object)proxy).Init(inner, gate);
        return proxy;
    }

    private sealed class HoldManagement : DispatchProxy
    {
        private IManagementService inner = null!;
        private CopyGate gate = null!;

        public void Init(IManagementService inner, CopyGate gate)
        {
            this.inner = inner;
            this.gate = gate;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is { Name: nameof(IManagementService.CopyDatabaseAsync) } copy)
            {
                return gate.HoldAsync(() => (Task<CopyDatabaseResult>)copy.Invoke(inner, args)!, (CancellationToken)args![^1]!);
            }

            return targetMethod!.Invoke(inner, args);
        }
    }

    private async Task<CopyDatabaseResult> HoldAsync(Func<Task<CopyDatabaseResult>> copy, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await copy().ConfigureAwait(false);
    }
}
