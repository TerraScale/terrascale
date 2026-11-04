using System.Buffers;
using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    internal Action<TenantContext>? Revised { get; init; }

    private async ValueTask IndexRevisionAsync()
    {
        if (_writerSession is not { } writer || Closed)
        {
            return;
        }

        try
        {
            if (await WriteLane.RunAsync(() => Revisions.AfterWrite(writer.Connection), CancellationToken.None).ConfigureAwait(false) && Tenant is { } tenant && !IsSystem)
            {
                Revised?.Invoke(tenant);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await RecordOperationDamageAsync(error).ConfigureAwait(false);
            LogAfterCommit("the revision index", error);
        }
    }

    internal async Task<RevisionPullResult> PullRevisionAsync(string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfRefused(maintenance: false);
        var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
        RevisionPullResult result;
        try
        {
            using var plan = await WithWriterAsync(writer =>
            {
                RefuseFaultedCopy();
                Revisions.Catch(writer.Connection);
                TursoRevisionLedger.BeginSnapshot(reader.Connection);
                try
                {
                    return Revisions.PlanPull(reader.Connection, clientRevision);
                }
                catch
                {
                    TursoRevisionLedger.EndSnapshot(reader.Connection);
                    throw;
                }
            }, "a revision pull", cancellationToken).ConfigureAwait(false);
            try
            {
                result = await StreamAsync(reader.Connection, plan, sink, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await io.RunAsync(() => TursoRevisionLedger.EndSnapshot(reader.Connection), CancellationToken.None).ConfigureAwait(false);
            }

            RefuseFaultedCopy();
        }
        catch (Exception error)
        {
            await RecordOperationDamageAsync(error).ConfigureAwait(false);
            await io.RunAsync(() => pool.Discard(reader), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ReturnAsync(reader).ConfigureAwait(false);
        return result;
    }

    private async Task<RevisionPullResult> StreamAsync(SqliteConnection reader, RevisionPullPlan plan, IRevisionPullSink sink, CancellationToken cancellationToken)
    {
        var cdcPosition = await io.RunAsync(() =>
        {
            using var command = reader.CreateCommand();
            command.CommandText = "SELECT coalesce((SELECT change_id FROM turso_cdc WHERE change_type = 2 ORDER BY change_id DESC LIMIT 1), 0);";
            return (long)command.ExecuteScalar()!;
        }, cancellationToken).ConfigureAwait(false);
        if (!await sink.StartAsync(new RevisionPullStart(plan.Revision, plan.Full, plan.DatabaseSize, plan.Count, plan.PageSize) { CdcPosition = cdcPosition }, cancellationToken).ConfigureAwait(false))
        {
            return new RevisionPullResult(plan.Revision, plan.Full, 0, plan.DatabaseSize) { CdcPosition = cdcPosition };
        }

        var numbers = ArrayPool<uint>.Shared.Rent(plan.BatchPages);
        var pages = ArrayPool<byte>.Shared.Rent(plan.BatchPages * plan.PageSize);
        try
        {
            var sent = 0;
            while (sent < plan.Count)
            {
                var start = sent;
                var count = await io.RunAsync(() => TursoRevisionLedger.ReadBatch(reader, plan, start, numbers, pages, cancellationToken), cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync(new RevisionPageBatch(numbers.AsMemory(0, count), pages.AsMemory(0, count * plan.PageSize), plan.PageSize), cancellationToken).ConfigureAwait(false);
                sent += count;
            }

            return new RevisionPullResult(plan.Revision, plan.Full, sent, plan.DatabaseSize) { CdcPosition = cdcPosition };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pages);
            ArrayPool<uint>.Shared.Return(numbers);
        }
    }

    private void RefuseFaultedCopy()
    {
        if (CloseRefusal is { } refusal)
        {
            throw new StorageCopyFaultedException(refusal);
        }
    }

    internal Task<IReadOnlyList<ReplicaPullObservation>> ReplicaStatusAsync(IReadOnlyList<string> regions, CancellationToken cancellationToken) =>
        WithWriterAsync(writer =>
        {
            RefuseFaultedCopy();
            Revisions.Catch(writer.Connection);
            return Revisions.ReplicaStatus(regions);
        }, "replica pull observations", cancellationToken);

    internal Task StartIncarnationAsync(CancellationToken cancellationToken) =>
        WithWriterAsync(writer =>
        {
            Revisions.StartIncarnation();
            Revisions.Attach(writer.Connection);
            return true;
        }, "a new revision incarnation", cancellationToken);
}
