using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal enum KeepReason
{
    MoveRolledBack,
    MoveUnproven,
    LeaseUnproven,
    HandoffPrepare,
    MoveTarget,
}

internal sealed record KeptCopy(
    string Id, string OrganizationId, string ProjectId, string DatabaseId, string? Revision, string Reason, DateTimeOffset KeptAt, IReadOnlyList<string> Files, long Bytes);

internal sealed class KeptCopies : IDisposable
{
    private const string RecordName = "kept.json";

    private readonly string _root;
    private readonly IStorageEngine _engine;
    private readonly IDatabaseRevisions _revisions;
    private readonly TimeProvider _time;
    private readonly ILogger<KeptCopies> _logger;
    private readonly Meter _meter;
    private readonly SemaphoreSlim _keeping = new(1, 1);

    public KeptCopies(TerraBaseOptions options, IStorageEngine engine, IDatabaseRevisions revisions, IMeterFactory meters, TimeProvider time, ILogger<KeptCopies> logger)
    {
        _root = Path.Combine(options.DataDirectory, KeptFiles.Directory);
        _engine = engine;
        _revisions = revisions;
        _time = time;
        _logger = logger;
        _meter = meters.Create(Telemetry.MeterName);
        _ = _meter.CreateObservableGauge("terrabase.home.kept.files", () => Measure().Files, "{file}",
            "Database files this node set aside and keeps without serving them; nothing automatic deletes them.");
        _ = _meter.CreateObservableGauge("terrabase.home.kept.bytes", () => Measure().Bytes, "By",
            "Bytes of the database files this node set aside and keeps without serving them.");
    }

    public void Dispose()
    {
        _keeping.Dispose();
        _meter.Dispose();
    }

    internal static string Wire(KeepReason reason) => reason switch
    {
        KeepReason.MoveRolledBack => "move_rolled_back",
        KeepReason.MoveUnproven => "move_unproven",
        KeepReason.LeaseUnproven => "lease_unproven",
        KeepReason.HandoffPrepare => "handoff_prepare",
        KeepReason.MoveTarget => "move_target",
        _ => throw new UnreachableException(),
    };

    internal async Task<KeptCopy?> KeepAsync(TenantContext database, KeepReason reason, CancellationToken cancellationToken)
    {
        await _keeping.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_engine.HasDatabaseFiles(database, cancellationToken))
            {
                return null;
            }

            var revision = await _revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
            var id = Ulid.NewUlid().ToString();
            var directory = Path.Combine(_root, id);
            var files = await _engine.SetAsideDatabaseAsync(database, directory, CancellationToken.None).ConfigureAwait(false);
            if (files.Count == 0)
            {
                return null;
            }

            var copy = new KeptCopy(id, database.OrganizationId, database.ProjectId!, database.DatabaseId!, revision?.ToString(), Wire(reason), _time.GetUtcNow(),
                [.. files.Select(Path.GetFileName).OfType<string>()], files.Sum(file => new FileInfo(file).Length));
            Write(directory, copy);
            _logger.ZLogWarning($"Kept a database copy this node does not serve: database {copy.DatabaseId:@Database} at {copy.Revision ?? "an unknown position":@Revision}, {copy.Bytes:@Bytes} bytes in {copy.Files.Count:@Files} files, reason {copy.Reason:@Reason}, set aside as {copy.Id:@Kept}; nothing automatic deletes it");
            return copy;
        }
        finally
        {
            _keeping.Release();
        }
    }

    internal IReadOnlyList<KeptCopy> List() => Directory.Exists(_root) ? [.. Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal).Select(Read)] : [];

    private (long Files, long Bytes) Measure()
    {
        if (!Directory.Exists(_root))
        {
            return (0, 0);
        }

        var stored = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(Stored).Select(file => new FileInfo(file)).ToList();
        return (stored.Count, stored.Sum(file => file.Length));
    }

    private static bool Stored(string file) => !Path.GetFileName(file).StartsWith(RecordName, StringComparison.Ordinal);

    private static KeptCopy Read(string directory)
    {
        var record = Path.Combine(directory, RecordName);
        var files = Directory.EnumerateFiles(directory).Where(Stored).Order(StringComparer.Ordinal).ToList();
        var bytes = files.Sum(file => new FileInfo(file).Length);
        var names = files.Select(Path.GetFileName).OfType<string>().ToList();
        var kept = File.Exists(record) ? JsonSerializer.Deserialize(File.ReadAllBytes(record), ReplicasJsonContext.Default.KeptCopy) : null;
        return kept is null
            ? new KeptCopy(Path.GetFileName(directory), "", "", "", null, "unrecorded", new DateTimeOffset(Directory.GetCreationTimeUtc(directory)), names, bytes)
            : kept with { Files = names, Bytes = bytes };
    }

    private static void Write(string directory, KeptCopy copy)
    {
        var path = Path.Combine(directory, RecordName);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, copy, ReplicasJsonContext.Default.KeptCopy);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
