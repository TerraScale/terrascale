using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

internal static class ReceiptImportProof
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var engine = services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
        var database = new TenantContext("01ARZ3NDEKTSV4RRFFQ69G5FAV", PlanTier.Hobby, "01ARZ3NDEKTSV4RRFFQ69G5FAW", "01ARZ3NDEKTSV4RRFFQ69G5FAX");
        var authority = services.GetRequiredService<TimeProvider>().GetUtcNow();
        const string holder = "journey-2";
        var purge = new DatabasePurgeRecord(database, authority, authority, authority, null, [holder]);
        var closure = new ProjectClosureRecord(database.ProjectId!, "01ARZ3NDEKTSV4RRFFQ69G5FAY", database.OrganizationId, authority, [holder]);
        var purgeKind = DatabasePurge.Receipts(database.DatabaseId!);
        var closureKind = ProjectClosure.Receipts(database.ProjectId!);
        await using (var transaction = await store.BeginDurableTransactionAsync(cancellationToken))
        {
            await CatalogTables.CreateAsync(transaction, cancellationToken);
            await SystemCatalog.PutAsync(transaction, DatabasePurge.Kind, database.DatabaseId!, purge, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken);
            await SystemCatalog.PutAsync(transaction, ProjectClosure.Kind, database.ProjectId!, closure, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken);
            await SystemCatalog.PutAsync(transaction, purgeKind, holder, new DatabasePurgeReceipt(database, holder, authority, Authority: authority), ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken);
            await SystemCatalog.PutAsync(transaction, closureKind, holder, new ProjectClosureReceipt(database.ProjectId!, closure.ClosureId, holder, true, authority, EvidenceVersion: 2), ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken);
            _ = await transaction.CommitAsync(cancellationToken);
        }
        var ordinary = await RegionalCatalog.ExportAsync(engine, cancellationToken);
        if (ordinary.Records.Any(record => record.Kind == purgeKind || record.Kind == closureKind))
        {
            throw new InvalidOperationException("ordinary handoff exported receipts before all receivers accept them");
        }
        var incoming = await SendingCatalog.ExportAsync(engine, cancellationToken);
        var receipts = incoming.Records.Where(record => record.Kind == purgeKind || record.Kind == closureKind).ToArray();
        if (receipts.Length != 2)
        {
            throw new InvalidOperationException("journey exporter did not produce both stored receipt kinds");
        }
        await using (var transaction = await store.BeginDurableTransactionAsync(cancellationToken))
        {
            _ = await transaction.DropKindAsync(purgeKind, cancellationToken);
            _ = await transaction.DropKindAsync(closureKind, cancellationToken);
            _ = await transaction.CommitAsync(cancellationToken);
        }
        await RegionalCatalog.ImportAsync(engine, incoming, cancellationToken);
        await using (var transaction = await store.BeginTransactionAsync(false, cancellationToken))
        {
            foreach (var receipt in receipts)
            {
                var stored = await transaction.GetAsync(receipt.Kind, receipt.Id, cancellationToken);
                if (stored is null || !stored.Json.Span.SequenceEqual(receipt.Json))
                {
                    throw new InvalidOperationException("import changed a receipt holder, authority or contents");
                }
            }
            var storedPurge = await SystemCatalog.GetAsync(transaction, purgeKind, holder, ManagementJsonContext.Default.DatabasePurgeReceipt, cancellationToken);
            var storedClosure = await SystemCatalog.GetAsync(transaction, closureKind, holder, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken);
            if (storedPurge is null || !storedPurge.Binds(purge, holder) || storedPurge.Binds(purge, "journey-4")
                || storedPurge.Binds(purge with { RequestedAt = authority.AddSeconds(1) }, holder)
                || storedClosure is null || !storedClosure.Confirms(closure, holder) || storedClosure.Confirms(closure, "journey-4")
                || storedClosure.Confirms(closure with { ClosureId = database.DatabaseId! }, holder))
            {
                throw new InvalidOperationException("imported receipt bindings do not match their original authority and holder");
            }
        }
        await RefusedAsync(incoming with { Records = [.. Enumerable.Repeat(receipts[0], 10_001)] });
        await RefusedAsync(incoming with { Records = [receipts[0] with { Json = new byte[4 * 1024 * 1024] }] });
        await RegionalCatalog.ImportAsync(engine, ordinary, cancellationToken);
        await using (var transaction = await store.BeginTransactionAsync(false, cancellationToken))
        {
            foreach (var receipt in receipts)
            {
                if (await transaction.GetAsync(receipt.Kind, receipt.Id, cancellationToken) is null)
                {
                    throw new InvalidOperationException("a receipt-free snapshot erased retained local receipt evidence");
                }
            }
        }
        Console.WriteLine("RECEIPT_IMPORT accepted=2 bindings=unchanged ordinary_export=0 retained=2 row_limit=rejected byte_limit=rejected");

        async Task RefusedAsync(RegionalCatalogSnapshot snapshot)
        {
            try
            {
                await RegionalCatalog.ImportAsync(engine, snapshot, cancellationToken);
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("handoff accepted receipt records beyond its snapshot limits");
        }
    }
}
