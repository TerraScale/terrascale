using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;

namespace TerraBase.Testing;

internal sealed class BackupShippingHost(ManagementApi api, FakeS3 s3, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
{
    public ManagementApi Api { get; } = api;

    public FakeS3 S3 { get; } = s3;

    public FakeTimeProvider Time { get; } = time;

    public ManagedDatabaseRef Database { get; } = database;

    public string File => $"v1/{Database.Organization}/{Database.Project}/{Database.Id}/nodes/local";

    private IContinuousBackup Backup => Api.Server.Services.GetRequiredService<IContinuousBackup>();
    public static async Task<BackupShippingHost> StartAsync(Action<IServiceCollection>? services = null)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var api = await ManagementApi.StartAsync(s3.Configure, registrations =>
        {
            s3.Route(registrations);
            ManualBackupHosting.Configure(registrations);
            services?.Invoke(registrations);
        }, time);
        try
        {

            var database = await api.CreateManagedDatabaseAsync("main");
            await api.SetPlanAsync(api.User, database.Organization, "enterprise");
            var data = await api.CreateClientAsync(database.Organization, "sql:read", "sql:write");
            foreach (var table in new[] { "people", "load" })
            {
                (await ManagementApi.SqlAsync(data, database, $"CREATE TABLE {table} (id TEXT PRIMARY KEY, text TEXT NOT NULL)")).EnsureSuccessStatusCode();
            }
            return new BackupShippingHost(api, s3, time, database, data);
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
    {
        Time.Advance(advance);
        await Backup.RunOnceAsync(cancellationToken);
    }

    public Task StartPassAsync(CancellationToken cancellationToken) => Backup.RunOnceAsync(cancellationToken);

    public BackupFileStatus Status() => Backup.Status.Files.Single(file => file.File == File);
    public IReadOnlyList<SegmentKey> Segments(string generation) =>
        [.. S3.Objects.Keys.Where(key => key.StartsWith($"{File}/wal/{generation}/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(key => BackupLayout.ParseSegment(key)!)];

    /// <summary>Upserts row <paramref name="name"/> of table <paramref name="table"/> (<c>people</c> or <c>load</c>).</summary>
    public async Task PutAsync(string table, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await SqlAsync(Database.Data, $"INSERT INTO {table} (id, text) VALUES (?, ?) ON CONFLICT (id) DO UPDATE SET text = excluded.text", [name, text], cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The text of row <paramref name="name"/> in the database named <paramref name="databaseName"/>, or null.</summary>
    public async Task<string?> ReadAsync(string databaseName, string table, string name, CancellationToken cancellationToken)
    {
        using var response = await SqlAsync((Database with { Name = databaseName }).Data, $"SELECT text FROM {table} WHERE id = ?", [name], cancellationToken);
        response.EnsureSuccessStatusCode();
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("results")[0].GetProperty("rows");
        return rows.GetArrayLength() == 0 ? null : rows[0][0].GetString();
    }

    private Task<HttpResponseMessage> SqlAsync(string path, string sql, string[] args, CancellationToken cancellationToken) =>
        data.PostAsJsonAsync(ManagementApi.Uri(path + "/sql/execute"), new { statements = new[] { new { sql, args } } }, cancellationToken);

    public async Task RestoreAsync(DateTimeOffset at, string name, CancellationToken cancellationToken)
    {
        using var owner = Api.ProjectClient(Database.Project);
        using var response = await owner.PostAsJsonAsync(ManagementApi.Uri($"{Database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(string.Create(CultureInfo.InvariantCulture, $"restore at {at:O}: {body}"));
    }

    public async Task<string> DatabaseIdAsync(string name, CancellationToken cancellationToken)
    {
        using var owner = Api.ProjectClient(Database.Project);
        using var response = await owner.GetAsync(ManagementApi.Uri($"/v1/projects/{Database.Project}/databases"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var listing = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return listing.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    public Task<string> MetricsAsync(CancellationToken cancellationToken) => Api.Server.Client.GetStringAsync(ManagementApi.Uri("/metrics"), cancellationToken);
    public static async Task AssertContiguousAsync(IReadOnlyList<SegmentKey> segments)
    {
        await Assert.That(segments.Count).IsGreaterThan(0);
        await Assert.That(segments[0].Start).IsEqualTo(0);
        for (var index = 1; index < segments.Count; index++)
        {
            await Assert.That(segments[index].Start).IsGreaterThan(segments[index - 1].Start);
            await Assert.That(segments[index].At).IsGreaterThanOrEqualTo(segments[index - 1].At);
        }
    }

    public async ValueTask DisposeAsync()
    {
        data.Dispose();
        await Api.DisposeAsync();
    }
}
