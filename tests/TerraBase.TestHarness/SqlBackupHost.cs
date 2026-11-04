using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Testing;

internal sealed class SqlBackupHost(ManagementApi api, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
{
    private long _stamped;

    public ManagementApi Api { get; } = api;

    public ManagedDatabaseRef Database { get; } = database;

    public DateTimeOffset SnapshotAt { get; private set; }

    public TenantContext Tenant => new(Database.Organization, PlanTier.Pro, Database.Project, Database.Id);

    private IContinuousBackup Backup => Api.Server.Services.GetRequiredService<IContinuousBackup>();

    public static async Task<SqlBackupHost> StartAsync(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        try
        {
            Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
            var database = await api.CreateManagedDatabaseAsync("main");
            await api.SetPlanAsync(api.User, database.Organization, "pro", cancellationToken);
            var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
            return new SqlBackupHost(api, time, database, api.DataClient(key));
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    public async Task SnapshotAsync(CancellationToken cancellationToken)
    {
        await ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        SnapshotAt = time.GetUtcNow();
        await StampAsync(null, cancellationToken);
    }

    public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
    {
        time.Advance(advance);
        await Backup.RunOnceAsync(cancellationToken);
    }

    public async Task StampAsync(DateTimeOffset? commit, CancellationToken cancellationToken)
    {
        var store = (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Tenant, cancellationToken))!;
        _stamped = await store.WithWriterAsync(writer =>
        {
            List<long> rows = [];
            using (var select = writer.Connection.CreateCommand())
            {
                select.CommandText = "SELECT change_id FROM turso_cdc WHERE change_id > $after ORDER BY change_id;";
                select.Parameters.AddWithValue("$after", _stamped);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(reader.GetInt64(0));
                }
            }

            if (commit is { } at)
            {
                Execute(writer, TursoSession.CaptureOff);
                try
                {
                    using var update = writer.Connection.CreateCommand();
                    update.CommandText = "UPDATE turso_cdc SET change_time = $time WHERE change_id > $after;";
                    update.Parameters.AddWithValue("$time", at.ToUnixTimeSeconds());
                    update.Parameters.AddWithValue("$after", _stamped);
                    update.ExecuteNonQuery();
                }
                finally
                {
                    Execute(writer, TursoSession.CaptureOn);
                }
            }

            return rows.Count > 0 ? rows[^1] : _stamped;
        }, "a test", cancellationToken);
    }

    public async Task<DateTimeOffset> LatestAsync(CancellationToken cancellationToken)
    {
        using var window = await Api.Client.GetAsync(ManagementApi.Uri($"{Database.Path}/point-in-time"), cancellationToken);
        var body = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(window.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        return body.GetProperty("latest").GetDateTimeOffset();
    }

    public async Task<string> RestoreAsync(DateTimeOffset at, string name, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{Database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body);
        return name;
    }

    public async Task<TenantContext> TenantAsync(string name, CancellationToken cancellationToken)
    {
        using var list = JsonDocument.Parse(await Api.Client.GetStringAsync(ManagementApi.Uri($"{Database.ProjectPath}/databases"), cancellationToken));
        var id = list.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
        return new TenantContext(Database.Organization, PlanTier.Pro, Database.Project, id);
    }

    public Task<JsonElement[]> OkAsync(params string[] statements) => OkInAsync(Database.Name, [.. statements.Select(sql => Stmt(sql))]);

    public Task<JsonElement[]> OkAsync(params object[] statements) => OkInAsync(Database.Name, statements);

    public async Task<JsonElement[]> OkInAsync(string name, params object[] statements)
    {
        var (status, body) = await PostAsync(name, statements);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        return [.. body.GetProperty("results").EnumerateArray()];
    }

    public async Task<string?> RefusedAsync(string name, params object[] statements)
    {
        var (status, body) = await PostAsync(name, statements);
        await Assert.That(status).IsNotEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        return body.GetProperty("error").TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public async Task<List<string>> RowsAsync(string name, string sql)
    {
        var results = await OkInAsync(name, Stmt(sql));
        return [.. results[0].GetProperty("rows").EnumerateArray().Select(row => string.Join('|', row.EnumerateArray().Select(Text)))];
    }

    public async ValueTask DisposeAsync()
    {
        data.Dispose();
        await Api.DisposeAsync();
    }

    private static void Execute(TursoSession writer, string sql)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => string.Empty,
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string name, object[] statements)
    {
        using var response = await data.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{Database.Project}/databases/{name}/sql/execute"), new { statements });
        return (response.StatusCode, JsonElement.Parse(await response.Content.ReadAsStringAsync()));
    }
    private static object Stmt(string sql, params object?[] args) => new { sql, args };
}
