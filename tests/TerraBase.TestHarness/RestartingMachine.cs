using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;

namespace TerraBase.Testing;

/// <summary>
/// One machine of a test cluster as production runs it (a backup bucket holds its catalog anchor), whose restarts reuse its
/// data directory and node name, as a rolling deploy restarts a region's task on the same volume.
/// </summary>
internal sealed class RestartingMachine(string region, FakeS3 bucket, FakeMemberStore members) : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-restarting-").FullName;
    private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
    private ManagementApi? _api;

    public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

    public ICatalogGate Gate => Api.Server.Services.GetRequiredService<ICatalogGate>();

    /// <summary>Starts this machine; <paramref name="services"/> replaces services for this start only.</summary>
    public async Task StartAsync(Action<IServiceCollection>? services = null) =>
        _api = await ManagementApi.StartAsync(settings =>
        {
            bucket.Configure(settings);
            settings["region"] = region;
            settings["node-name"] = _name;
            settings["data-directory"] = _directory;
        }, collection =>
        {
            bucket.Route(collection);
            ManualBackupHosting.Configure(collection);
            collection.AddSingleton<IMemberDirectory>(members);
            services?.Invoke(collection);
        });

    /// <summary>A graceful stop: the host's shutdown runs, as on SIGTERM within its grace period.</summary>
    public async Task StopAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
            _api = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
