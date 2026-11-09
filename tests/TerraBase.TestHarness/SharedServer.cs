namespace TerraBase.Testing;

public static class SharedServer
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static readonly Lazy<Task<ManagementApi>> Server = new(StartAsync);

    public static async Task WarmAsync()
    {
        var api = await Server.Value;
        var database = await api.CreateManagedDatabaseAsync();
        using var data = api.DataClient(await api.Server.IssueAsync(database.Organization, "sql:read", "sql:write"));
        using var query = await ManagementApi.SqlAsync(data, database, "SELECT 1");
        query.EnsureSuccessStatusCode();
        using var dropped = await api.ProjectClient(database.Project).DeleteAsync(ManagementApi.Uri(database.Path));
        dropped.EnsureSuccessStatusCode();
        using var project = await api.Client.DeleteAsync(ManagementApi.Uri(database.ProjectPath));
        project.EnsureSuccessStatusCode();
        using var organization = await api.Client.DeleteAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}"));
        organization.EnsureSuccessStatusCode();
    }

    internal static async Task<ManagementApi> GetAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None, cancellationToken);
        return await Server.Value.WaitAsync(linked.Token);
    }

    public static async Task InitializeAsync() => _ = await Server.Value;

    private static Task<ManagementApi> StartAsync()
    {
        using var flow = ExecutionContext.SuppressFlow();
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
