using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalReceiptImports(IMeterFactory meters, ILogger<RegionalReceiptImports> logger)
{
    private const string Purges = "database_purge_receipts";
    private const string Closures = "project_closure_receipts";
    private readonly Counter<long> _conflicts = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.regional_catalog.receipt_conflicts");

    internal async Task<IReadOnlyList<RegionalRecord>> ValidateAsync(IStorageTransaction transaction, IReadOnlyList<RegionalRecord> records, CancellationToken cancellationToken)
    {
        var incoming = records.ToLookup(record => (record.Kind, record.Id));
        Dictionary<(string Kind, string Id), RegionalRecord> additions = [];
        Dictionary<string, int> conflicts = [];
        foreach (var record in records.Where(record => record.Kind.StartsWith(Purges + "/", StringComparison.Ordinal)
            || record.Kind.StartsWith(Closures + "/", StringComparison.Ordinal)))
        {
            var kind = record.Kind.StartsWith(Purges + "/", StringComparison.Ordinal) ? Purges : Closures;
            var stored = await transaction.GetAsync(record.Kind, record.Id, cancellationToken).ConfigureAwait(false);
            if (!Valid(record, kind, incoming) || stored is not null && !stored.Json.Span.SequenceEqual(record.Json)
                || additions.TryGetValue((record.Kind, record.Id), out var prior) && !prior.Json.AsSpan().SequenceEqual(record.Json))
            {
                conflicts[kind] = conflicts.GetValueOrDefault(kind) + 1;
            }
            else if (stored is null)
            {
                additions.TryAdd((record.Kind, record.Id), record);
            }
        }
        if (conflicts.Count > 0)
        {
            foreach (var (kind, count) in conflicts)
            {
                _conflicts.Add(count, new KeyValuePair<string, object?>("kind", kind));
                LogConflict(logger, kind, count);
            }
            throw new InvalidDataException("regional catalog receipt conflict: " + string.Join(", ", conflicts.Select(pair => $"{pair.Key} ({pair.Value} records)")));
        }
        return [.. additions.Values];
    }

    private static bool Valid(RegionalRecord record, string kind, ILookup<(string Kind, string Id), RegionalRecord> incoming)
    {
        try
        {
            var id = record.Kind[(kind.Length + 1)..];
            var authorities = incoming[(kind == Purges ? DatabasePurge.Kind : ProjectClosure.Kind, id)].ToArray();
            if (id.Length == 0 || record.Id.Length == 0 || authorities.Length != 1)
            {
                return false;
            }
            _ = StrictJson.Read(record.Json, requireObject: true);
            _ = StrictJson.Read(authorities[0].Json, requireObject: true);
            if (kind == Purges)
            {
                var receipt = JsonSerializer.Deserialize(record.Json, ManagementJsonContext.Default.DatabasePurgeReceipt);
                var authority = JsonSerializer.Deserialize(authorities[0].Json, ManagementJsonContext.Default.DatabasePurgeRecord);
                return receipt is { Database: not null, Authority: not null } && authority is { FormatVersion: 1, Database: not null }
                    && record.Kind == DatabasePurge.Receipts(receipt.Database.DatabaseId!) && receipt.Binds(authority, record.Id);
            }
            var closureReceipt = JsonSerializer.Deserialize(record.Json, ManagementJsonContext.Default.ProjectClosureReceipt);
            var closure = JsonSerializer.Deserialize(authorities[0].Json, ManagementJsonContext.Default.ProjectClosureRecord);
            return closureReceipt is not null && closure is { FormatVersion: 1 or 2, ClosureId.Length: > 0 }
                && record.Kind == ProjectClosure.Receipts(closureReceipt.ProjectId)
                && (closureReceipt.Confirms(closure, record.Id) || closureReceipt.Refuses(closure, record.Id));
        }
        catch (Exception error) when (error is JsonException or JsonValidationException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional catalog import refused: {Kind} has {Count} invalid or conflicting receipt records; catalog unchanged")]
    private static partial void LogConflict(ILogger logger, string kind, int count);
}
