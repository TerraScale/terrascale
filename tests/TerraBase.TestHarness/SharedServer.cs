namespace TerraBase.Testing;

/// <summary>Owns the default server and its temporary data directory until all tests in this assembly finish.</summary>
public static class SharedServer
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static readonly Lazy<Task<ManagementApi>> Server = new(StartAsync);

    /// <summary>Starts the assembly-owned server before parallel test requests arrive.</summary>
    public static async Task WarmAsync() => await Server.Value;

    internal static async Task<ManagementApi> GetAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.Execution.CancellationToken, cancellationToken);
        return await Server.Value.WaitAsync(linked.Token);
    }

    private static Task<ManagementApi> StartAsync()
    {
        // The host belongs to the assembly, so its background work must not inherit the first test's context or timeout.
        using var flow = ExecutionContext.SuppressFlow();
        // A shared host serves the whole suite; use the production CPU-derived worker count.
        return Task.Run(() => ManagementApi.StartAsync(settings => settings.Remove("storage-threads"),
            lifetimeCancellationToken: Lifetime.Token));
    }

    [After(HookType.Assembly)]
    public static async Task StopAsync()
    {
        try
        {
            if (Server.IsValueCreated)
            {
                if (!Server.Value.IsCompleted)
                {
                    await Lifetime.CancelAsync();
                }

                await using var server = await Server.Value;
            }
        }
        finally
        {
            Lifetime.Dispose();
        }
    }
}
