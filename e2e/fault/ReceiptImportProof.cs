using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;
using TerraBase.Primitives;

internal static class ReceiptImportProof
{
    private const string Holder = "receipt-source";
    private static readonly TenantContext Database = new("01ARZ3NDEKTSV4RRFFQ69G5FAV", PlanTier.Hobby, "01ARZ3NDEKTSV4RRFFQ69G5FAW", "01ARZ3NDEKTSV4RRFFQ69G5FAX");
    private static readonly DateTimeOffset Authority = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DatabasePurgeRecord Purge = new(Database, Authority, Authority, Authority, null, [Holder]);
    private static readonly ProjectClosureRecord Closure = new(Database.ProjectId!, "01ARZ3NDEKTSV4RRFFQ69G5FAY", Database.OrganizationId, Authority, [Holder]);
    private static readonly DatabasePurgeReceipt PurgeReceipt = new(Database, Holder, Authority, Authority: Authority);
    private static readonly ProjectClosureReceipt ClosureReceipt = new(Database.ProjectId!, Closure.ClosureId, Holder, true, Authority, EvidenceVersion: 2);
    private static readonly string PurgeKind = DatabasePurge.Receipts(Database.DatabaseId!);
    private static readonly string ClosureKind = ProjectClosure.Receipts(Database.ProjectId!);
    private const string MarkerKind = "api_key_hashes";
    private const string Marker = "receipt-import-marker";
    private static readonly Dictionary<string, object?> NoNamed = [];

    public static async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("E2E_RECEIPT_ROLE") is not null)
        {
            await RunNodeAsync(app, cancellationToken);
            return;
        }
        var services = app.Services;
        var engine = services.GetRequiredService<IStorageEngine>();
        await SeedAsync(engine, "identical", cancellationToken);
        var ordinary = await RegionalCatalog.ExportAsync(engine, cancellationToken);
        var incoming = await SendingCatalog.ExportAsync(engine, cancellationToken);
        var receipts = incoming.Records.Where(record => record.Kind == PurgeKind || record.Kind == ClosureKind).ToArray();
        Require(receipts.Length == 2 && !ordinary.Records.Any(record => record.Kind == PurgeKind || record.Kind == ClosureKind),
            "ordinary handoff exported receipts or journey exporter omitted them");
        await ImportAsync(incoming);
        await SameAsync(engine, incoming, cancellationToken);
        Console.WriteLine("RECEIPT_CASE identical passed");

        List<string> failed = [];
        foreach (var name in new[] { "purge-holder", "purge-authority", "closure-holder", "closure-authority", "stale-closure", "stale-refusal",
            "absent-holder", "absent-authority", "absent-database", "absent-project", "purge-version", "closure-version", "evidence-version", "malformed", "duplicate", "conflict-count" })
        {
            var before = await SendingCatalog.ExportAsync(engine, cancellationToken);
            var bad = Changed(incoming, name);
            var refused = false;
            try
            {
                await ImportAsync(bad);
            }
            catch (Exception error) when (error is InvalidDataException or JsonValidationException)
            {
                var kind = name.StartsWith("purge", StringComparison.Ordinal) || name is "absent-holder" or "absent-authority" or "absent-database" or "malformed" or "duplicate" or "conflict-count"
                    ? "database_purge_receipts" : "project_closure_receipts";
                refused = error.Message.Contains(kind, StringComparison.Ordinal) && error.Message.Contains(name == "conflict-count" ? "2 records" : "1 records", StringComparison.Ordinal);
            }
            var after = await SendingCatalog.ExportAsync(engine, cancellationToken);
            var unchanged = Bytes(before).AsSpan().SequenceEqual(Bytes(after));
            if (!refused || !unchanged)
            {
                failed.Add(name);
                Console.WriteLine($"RECEIPT_CASE {name} FAILED refused={refused} unchanged={unchanged}");
                await SeedAsync(engine, "identical", cancellationToken);
                await ImportAsync(incoming);
            }
            else
            {
                Console.WriteLine($"RECEIPT_CASE {name} passed");
            }
        }

        await ImportAsync(ordinary);
        await using (var transaction = await (await engine.OpenSystemDatabaseAsync(cancellationToken)).BeginTransactionAsync(false, cancellationToken))
        {
            foreach (var receipt in receipts)
            {
                var stored = await transaction.GetAsync(receipt.Kind, receipt.Id, cancellationToken);
                Require(stored is not null && stored.Json.Span.SequenceEqual(receipt.Json), "receipt-free import changed retained receipt bytes");
            }
        }
        Console.WriteLine("RECEIPT_CASE retention-bytes passed");
        var absent = Record(PurgeKind, "receipt-new", PurgeReceipt with { MachineId = "receipt-new" });
        await ImportAsync(incoming with { Records = [.. incoming.Records, absent] });
        await using (var transaction = await (await engine.OpenSystemDatabaseAsync(cancellationToken)).BeginTransactionAsync(false, cancellationToken))
        {
            var stored = await transaction.GetAsync(absent.Kind, absent.Id, cancellationToken);
            Require(stored is not null && stored.Json.Span.SequenceEqual(absent.Json), "absent valid receipt was not stored byte for byte");
        }
        Console.WriteLine("RECEIPT_CASE absent-valid passed");
        Require(failed.Count == 0, "receipt cases failed: " + string.Join(", ", failed));
        Console.WriteLine("RECEIPT_IMPORT identical=accepted invalid=16 retained_bytes=2 ordinary_export=0");

        Task ImportAsync(RegionalCatalogSnapshot snapshot) =>
            RegionalCatalog.ImportAsync(engine, snapshot, services.GetRequiredService<RegionalReceiptImports>(), cancellationToken);
    }

    private static async Task RunNodeAsync(WebApplication app, CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken);
        var engine = app.Services.GetRequiredService<IStorageEngine>();
        RegionalCatalogSnapshot? before = null;
        try
        {
            Console.WriteLine("RECEIPT_NODE started");
            while (await Console.In.ReadLineAsync(cancellationToken) is { } command && command != "stop")
            {
                if (command.StartsWith("stage ", StringComparison.Ordinal))
                {
                    await SeedAsync(engine, command[6..], cancellationToken);
                }
                else if (command == "capture")
                {
                    before = await SendingCatalog.ExportAsync(engine, cancellationToken);
                }
                else if (command == "unchanged")
                {
                    var after = await SendingCatalog.ExportAsync(engine, cancellationToken);
                    Require(before is not null && Bytes(before).AsSpan().SequenceEqual(Bytes(after)),
                        "refused handoff changed the receiving catalog: " + Difference(before, after));
                }
                else if (command == "receipts")
                {
                    await using var transaction = await (await engine.OpenSystemDatabaseAsync(cancellationToken)).BeginTransactionAsync(false, cancellationToken);
                    var purge = await transaction.GetAsync(PurgeKind, Holder, cancellationToken);
                    var closure = await transaction.GetAsync(ClosureKind, Holder, cancellationToken);
                    Require(purge is not null && purge.Json.Span.SequenceEqual(Record(PurgeKind, Holder, PurgeReceipt).Json)
                        && closure is not null && closure.Json.Span.SequenceEqual(Record(ClosureKind, Holder, ClosureReceipt).Json),
                        "identical handoff changed receipt bytes");
                }
                else if (command != "ping")
                {
                    throw new InvalidOperationException("unknown receipt journey command");
                }
                Console.WriteLine("RECEIPT_NODE " + command);
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task SeedAsync(IStorageEngine engine, string name, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken);
        await CatalogTables.CreateAsync(transaction, cancellationToken);
        _ = await transaction.DeleteAsync(PurgeKind, "receipt-absent", cancellationToken);
        _ = await transaction.DeleteAsync(ClosureKind, "receipt-absent", cancellationToken);
        _ = await transaction.DropKindAsync(MarkerKind, cancellationToken);
        _ = await transaction.ExecuteStatementAsync(Sql("DELETE FROM catalog_retired_ids WHERE id = ?", Marker), cancellationToken);
        await SystemCatalog.PutAsync(transaction, DatabasePurge.Kind, Database.DatabaseId!, Purge, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken);
        await SystemCatalog.PutAsync(transaction, ProjectClosure.Kind, Database.ProjectId!, Closure, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken);
        await SystemCatalog.PutAsync(transaction, PurgeKind, Holder, PurgeReceipt, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken);
        await SystemCatalog.PutAsync(transaction, ClosureKind, Holder, ClosureReceipt, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken);
        if (name is "rows" or "bytes")
        {
            var rows = name == "rows" ? 10_001 : 5;
            var json = name == "bytes" ? Encoding.UTF8.GetBytes("\"" + new string('x', 512 * 1024) + "\"") : "{}"u8.ToArray();
            for (var i = 0; i < rows; i++)
            {
                _ = await transaction.PutAsync(MarkerKind, i.ToString(CultureInfo.InvariantCulture), json, cancellationToken);
            }
        }
        else if (name != "identical")
        {
            var changed = Changed(new RegionalCatalogSnapshot([], [
                Record(PurgeKind, Holder, PurgeReceipt), Record(ClosureKind, Holder, ClosureReceipt)
            ]), name);
            foreach (var record in changed.Records)
            {
                _ = await transaction.PutAsync(record.Kind, record.Id, record.Json, cancellationToken);
            }
            _ = await transaction.ExecuteStatementAsync(Sql("INSERT INTO catalog_retired_ids VALUES (?)", Marker), cancellationToken);
        }
        _ = await transaction.CommitAsync(cancellationToken);
    }

    private static RegionalCatalogSnapshot Changed(RegionalCatalogSnapshot snapshot, string name)
    {
        var purge = snapshot.Records.Single(record => record.Kind == PurgeKind);
        var wrongDatabase = Database with { DatabaseId = "01ARZ3NDEKTSV4RRFFQ69G5FAZ" };
        var change = name switch
        {
            "purge-holder" => Record(PurgeKind, Holder, PurgeReceipt with { MachineId = "receipt-other" }),
            "purge-authority" => Record(PurgeKind, Holder, PurgeReceipt with { Authority = Authority.AddSeconds(-1) }),
            "closure-holder" => Record(ClosureKind, Holder, ClosureReceipt with { MachineId = "receipt-other" }),
            "closure-authority" => Record(ClosureKind, Holder, ClosureReceipt with { ClosureId = Database.DatabaseId! }),
            "stale-closure" => Record(ClosureKind, Holder, ClosureReceipt with { VerifiedAt = Authority.AddSeconds(-1) }),
            "stale-refusal" => Record(ClosureKind, Holder, ClosureReceipt with { Empty = false, VerifiedAt = Authority.AddSeconds(-1) }),
            "absent-holder" => Record(PurgeKind, "receipt-absent", PurgeReceipt),
            "absent-authority" => Record(PurgeKind, "receipt-absent", PurgeReceipt with { MachineId = "receipt-absent", Authority = Authority.AddSeconds(-1) }),
            "absent-database" => Record(PurgeKind, "receipt-absent", PurgeReceipt with { MachineId = "receipt-absent", Database = wrongDatabase }),
            "absent-project" => Record(ClosureKind, "receipt-absent", ClosureReceipt with { MachineId = "receipt-absent", ProjectId = Database.DatabaseId! }),
            "purge-version" => Record(PurgeKind, Holder, PurgeReceipt with { FormatVersion = 2 }),
            "closure-version" => Record(ClosureKind, Holder, ClosureReceipt with { FormatVersion = 2 }),
            "evidence-version" => Record(ClosureKind, Holder, ClosureReceipt with { EvidenceVersion = 1 }),
            "malformed" => purge with { Json = "{"u8.ToArray() },
            "duplicate" => Record(PurgeKind, Holder, PurgeReceipt with { VerifiedAt = Authority.AddSeconds(-1) }),
            "conflict-count" => Record(PurgeKind, Holder, PurgeReceipt with { MachineId = "receipt-other" }),
            _ => throw new InvalidOperationException("unknown receipt case"),
        };
        var records = snapshot.Records.Where(record => name == "duplicate" || record.Kind != change.Kind || record.Id != change.Id).ToList();
        records.Add(change);
        if (name == "conflict-count")
        {
            records.Add(Record(PurgeKind, "receipt-absent", PurgeReceipt));
        }
        records.Add(new RegionalRecord(MarkerKind, Marker, "{}"u8.ToArray()));
        var tables = snapshot.Tables.Select(table => table.Name == "catalog_retired_ids"
            ? table with { Rows = [.. table.Rows, [new RegionalCell(Marker, null)]] } : table).ToArray();
        return snapshot with { Tables = tables, Records = records };
    }

    private static RegionalRecord Record(string kind, string holder, DatabasePurgeReceipt receipt) =>
        new(kind, holder, JsonSerializer.SerializeToUtf8Bytes(receipt, ManagementJsonContext.Default.DatabasePurgeReceipt));

    private static RegionalRecord Record(string kind, string holder, ProjectClosureReceipt receipt) =>
        new(kind, holder, JsonSerializer.SerializeToUtf8Bytes(receipt, ManagementJsonContext.Default.ProjectClosureReceipt));

    private static byte[] Bytes(RegionalCatalogSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot with
    {
        Tables = [.. snapshot.Tables.Select(table => table with
        {
            Rows = [.. table.Rows.OrderBy(row => string.Join('|', row.Select(cell => JsonSerializer.Serialize(cell, ReplicasJsonContext.Default.RegionalCell))), StringComparer.Ordinal)]
        })],
        Records = [.. snapshot.Records.OrderBy(record => record.Kind, StringComparer.Ordinal).ThenBy(record => record.Id, StringComparer.Ordinal)]
    }, ReplicasJsonContext.Default.RegionalCatalogSnapshot);

    private static string Difference(RegionalCatalogSnapshot? before, RegionalCatalogSnapshot after)
    {
        if (before is null)
        {
            return "capture missing";
        }
        var tables = before.Tables.Where(table => !Bytes(new([table], [])).AsSpan().SequenceEqual(Bytes(new([after.Tables.Single(candidate => candidate.Name == table.Name)], []))))
            .Select(table => table.Name);
        var kinds = before.Records.Concat(after.Records).Select(record => record.Kind).Distinct(StringComparer.Ordinal)
            .Where(kind => !Bytes(new([], [.. before.Records.Where(record => record.Kind == kind)])).AsSpan()
                .SequenceEqual(Bytes(new([], [.. after.Records.Where(record => record.Kind == kind)]))))
            .Select(kind => kind.Split('/')[0]);
        return "tables=" + string.Join(',', tables) + "; kinds=" + string.Join(',', kinds);
    }

    private static async Task SameAsync(IStorageEngine engine, RegionalCatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var transaction = await (await engine.OpenSystemDatabaseAsync(cancellationToken)).BeginTransactionAsync(false, cancellationToken);
        foreach (var receipt in snapshot.Records.Where(record => record.Kind == PurgeKind || record.Kind == ClosureKind))
        {
            var stored = await transaction.GetAsync(receipt.Kind, receipt.Id, cancellationToken);
            Require(stored is not null && stored.Json.Span.SequenceEqual(receipt.Json), "identical receipt import changed receipt bytes");
        }
    }

    private static SqlStatement Sql(string text, params object?[] args) => new(text, args, NoNamed, 1, 1024, TimeSpan.FromSeconds(10));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
