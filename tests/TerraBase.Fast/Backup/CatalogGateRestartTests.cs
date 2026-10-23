using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// A region restarted while the others serve opens its catalog gate within seconds, as production runs it (a backup bucket
/// holds each machine's catalog anchor), and only on a catalog that holds what the others committed while it was down.
/// </summary>
public sealed class CatalogGateRestartTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_restarted_region_opens_its_catalog_gate_within_seconds(bool anotherDown)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new Machine("gru", bucket, members);
        await using var sjc = new Machine("sjc", bucket, members);
        await using var fra = new Machine("fra", bucket, members);
        foreach (var machine in (Machine[])[gru, sjc, fra])
        {
            await machine.StartAsync();
        }

        foreach (var machine in (Machine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        // fra is down too when anotherDown: gru then opens from gru and sjc, a majority its anchor allows.
        if (anotherDown)
        {
            await fra.StopAsync();
        }

        await gru.StopAsync();
        var missed = await sjc.Api.CreateOrganizationAsync("written while gru was down");

        var restarted = Stopwatch.GetTimestamp();
        await gru.StartAsync();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gru.Gate.IsOpen), timeout: Within);
        Console.WriteLine($"gru's catalog gate opened {Stopwatch.GetElapsedTime(restarted).TotalMilliseconds:F0} ms after its restart (another down: {anotherDown})");

        // Open means verified: the organization sjc created meanwhile is in gru's catalog.
        var catalog = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await catalog.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("organizations", missed, token)).IsNotNull();
    }

    // One machine whose restarts reuse its data directory and node name.
    private sealed class Machine(string region, FakeS3 bucket, FakeMemberStore members) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-gate-restart-").FullName;
        private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private ManagementApi? _api;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public ICatalogGate Gate => Api.Server.Services.GetRequiredService<ICatalogGate>();

        public async Task StartAsync() =>
            _api = await ManagementApi.StartAsync(settings =>
            {
                bucket.Configure(settings);
                settings["region"] = region;
                settings["node-name"] = _name;
                settings["data-directory"] = _directory;
            }, services =>
            {
                bucket.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<IMemberDirectory>(members);
            });

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
}
