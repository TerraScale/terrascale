using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrRepro;

/// <summary>
/// One in-process node backing up to the in-memory bucket, shipped only when a reproduction runs a pass. Evidence for the
/// continuous-backup / point-in-time-restore defect reproductions; they skip unless <see cref="Variable"/> is 1.
/// </summary>
internal sealed class ReproHost(ManagementApi api, FakeS3 s3, FakeTimeProvider time) : IAsyncDisposable
{
    public const string Variable = "TERRABASE_PITR_REPRO";

    public ManagementApi Api { get; } = api;

    public FakeS3 S3 { get; } = s3;

    public FakeTimeProvider Time { get; } = time;

    public IContinuousBackup Backup => Api.Server.Services.GetRequiredService<IContinuousBackup>();

    public static void SkipUnlessRequested() =>
        Skip.When(Environment.GetEnvironmentVariable(Variable) != "1", $"a PITR defect reproduction: run it with {Variable}=1");

    public static async Task<ReproHost> StartAsync(Action<IServiceCollection>? services = null)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var api = await ManagementApi.StartAsync(s3.Configure, collection =>
        {
            s3.Route(collection);
            ManualBackupHosting.Configure(collection);
            services?.Invoke(collection);
        }, time);
        return new ReproHost(api, s3, time);
    }

    public static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    public static void Log(string line) => Console.WriteLine("[pitr-repro] " + line);

    /// <summary>Advances the clock, then runs one shipping pass of every file.</summary>
    public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
    {
        Time.Advance(advance);
        await Backup.RunOnceAsync(cancellationToken);
    }

    public BackupFileStatus Status(ManagedDatabaseRef database) => Backup.Status.Files.Single(file => file.File == File(database));

    public string StatusLine(ManagedDatabaseRef database)
    {
        var status = Status(database);
        return $"generation={status.Generation ?? "null"} logBytes={status.LogBytes} pendingBytes={status.PendingBytes} "
            + $"lastShippedAt={status.LastShippedAt:O} lagSeconds={status.LagSeconds} lastError={status.LastError ?? "null"}";
    }

    /// <summary>Every segment object of a generation in the bucket, in key (start, then time) order.</summary>
    public IReadOnlyList<SegmentKey> Segments(ManagedDatabaseRef database, string generation) =>
        [.. S3.Objects.Keys.Where(key => key.StartsWith($"{File(database)}/wal/{generation}/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(key => BackupLayout.ParseSegment(key)!)];

    /// <summary>Deletes a bucket object through the server's own object store (a real S3 DELETE to the fake).</summary>
    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken) =>
        Api.Server.Services.GetRequiredService<S3ObjectStore>().DeleteAsync(key, cancellationToken);

    public static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string collection, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, collection, name)), new { text }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"PUT {name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }
    }

    /// <summary>The status of reading document <paramref name="name"/> from the database named <paramref name="databaseName"/>.</summary>
    public static async Task<HttpStatusCode> ReadAsync(HttpClient data, ManagedDatabaseRef database, string databaseName, string collection, string name,
        CancellationToken cancellationToken)
    {
        using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database with { Name = databaseName }, collection, name)), cancellationToken);
        return response.StatusCode;
    }

    public async Task<(HttpStatusCode Status, string Body)> RestoreAsync(ManagedDatabaseRef database, DateTimeOffset at, string name, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<string> WindowAsync(ManagedDatabaseRef database, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.GetAsync(ManagementApi.Uri($"{database.Path}/point-in-time"), cancellationToken);
        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}";
    }

    public async Task<DateTimeOffset> LatestAsync(ManagedDatabaseRef database, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.GetAsync(ManagementApi.Uri($"{database.Path}/point-in-time"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    public async Task<string> PlanAsync(ManagedDatabaseRef database, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/plan"), new { pointInTime = at }, cancellationToken);
        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}";
    }

    /// <summary>
    /// Restores straight through <see cref="IPointInTimeRestore"/> into a scratch engine database (deleted again) to see the
    /// exception the HTTP route turns into a 500: "restored", or the exception's type and message.
    /// </summary>
    public async Task<string> RestoreDirectAsync(ManagedDatabaseRef database, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var source = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var target = source with { DatabaseId = EntityIds.New().ToString() };
        try
        {
            var result = await Api.Server.Services.GetRequiredService<IPointInTimeRestore>().RestoreAsync(source, at, target, cancellationToken);
            return result is Done ? "restored" : "refused";
        }
        catch (InvalidDataException error)
        {
            return $"{nameof(InvalidDataException)}: {error.Message}";
        }
        finally
        {
            await Api.Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(target, CancellationToken.None);
        }
    }

    /// <summary>The /metrics lines of the backup counters (generations, holes, failures, uploaded bytes).</summary>
    public async Task<string> CountersAsync(CancellationToken cancellationToken)
    {
        var metrics = await Api.Server.Client.GetStringAsync(ManagementApi.Uri("/metrics"), cancellationToken);
        return string.Join(" | ", metrics.Split('\n').Where(line => line.StartsWith("terrabase_backup_generations", StringComparison.Ordinal)
            || line.StartsWith("terrabase_backup_holes", StringComparison.Ordinal) || line.StartsWith("terrabase_backup_failures", StringComparison.Ordinal)
            || line.StartsWith("terrabase_backup_uploaded", StringComparison.Ordinal)));
    }

    public ValueTask DisposeAsync() => Api.DisposeAsync();
}
