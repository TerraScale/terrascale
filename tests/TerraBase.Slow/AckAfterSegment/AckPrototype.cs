using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.AckAfterSegment;

internal sealed class AckPrototype : IAsyncDisposable
{
    public const string Collection = "items";
    private const string Prefix = "ack-proto/generation-1";
    private readonly ManagementApi _api;
    private readonly ServiceProvider _clients;
    private readonly string _scratch;

    private AckPrototype(ManagementApi api, ServiceProvider clients, S3ObjectStore store, ManagedDatabaseRef database, HttpClient data, string scratch)
    {
        (_api, _clients, Store, Database, Data, _scratch) = (api, clients, store, database, data, scratch);
    }

    public S3ObjectStore Store { get; }

    public ManagedDatabaseRef Database { get; }

    public HttpClient Data { get; }

    private IWalBackup Backup => _api.Server.Services.GetRequiredService<IWalBackup>();

    public static async Task<AckPrototype> StartAsync(ObjectStoreFaults faults, CancellationToken cancellationToken)
    {
        var serverBucket = new FakeS3();
        var api = await ManagementApi.StartAsync(serverBucket.Configure, services =>
        {
            serverBucket.Route(services);
            ManualBackupHosting.Configure(services);
        });
        ServiceProvider? clients = null;
        try
        {

            var database = await api.CreateManagedDatabaseAsync();
            await api.SetPlanAsync(api.User, database.Organization, "enterprise", cancellationToken);
            var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, Collection, cancellationToken);

            var bucket = new FakeS3();
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            bucket.Configure(settings);
            var services = new ServiceCollection();
            bucket.RoutePlatform(services);
            _ = services.AddHttpClient(S3ObjectStore.HttpClientName).AddHttpMessageHandler(() => new ObjectStoreFaults.Handler(faults));
            clients = services.BuildServiceProvider();
            var store = new S3ObjectStore(new Uri(settings["backup-endpoint"]), settings["backup-region"], settings["backup-bucket"],
                settings["backup-access-key-id"], settings["backup-secret-key"], clients.GetRequiredService<IHttpClientFactory>(),
                S3ObjectStore.HttpClientName, TimeProvider.System);
            return new AckPrototype(api, clients, store, database, data, Directory.CreateTempSubdirectory("ack-proto-").FullName);
        }
        catch
        {
            if (clients is not null)
            {
                await clients.DisposeAsync();
            }

            await api.DisposeAsync();
            throw;
        }
    }

    public Task<SegmentAckGate> GateAsync(CancellationToken cancellationToken, TimeProvider? time = null) =>
        SegmentAckGate.StartAsync(Backup, new TenantContext(Database.Organization, PlanTier.Enterprise, Database.Project, Database.Id),
            Store, Prefix, _scratch, time ?? TimeProvider.System, cancellationToken);
    public async Task<string> WriteAsync(CancellationToken cancellationToken)
    {
        var id = EntityIds.New().ToString();
        using var response = await Data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(Database, Collection, id)), new { n = 1 }, cancellationToken);
        _ = response.EnsureSuccessStatusCode();
        return id;
    }

    public async Task<(HashSet<string> Ids, long Position, int Segments)> RestoreAsync(CancellationToken cancellationToken)
    {
        var (file, position, segments) = await SegmentHistory.RestoreAsync(Store, Backup, Prefix, _scratch, cancellationToken);
        HashSet<string> ids = [with(StringComparer.Ordinal)];
        await using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false })))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id FROM \"{Collection}\"";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                _ = ids.Add(reader.GetString(0));
            }
        }

        File.Delete(file);
        File.Delete(file + DatabaseKeys.Suffix);
        return (ids, position, segments);
    }

    public async ValueTask DisposeAsync()
    {
        Data.Dispose();
        Store.Dispose();
        await _clients.DisposeAsync();
        await _api.DisposeAsync();
        Directory.Delete(_scratch, recursive: true);
    }
}
