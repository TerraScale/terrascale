using System.Buffers;
using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    internal Action<TenantContext>? Revised { get; init; }

    private async ValueTask IndexRevisionAsync()
    {
        if (_writerSession is not { } writer || Closed || CloseRefusal is not null)
        {
            return;
        }

        using var refused = ReadCancellation(CancellationToken.None);
        using var interrupted = refused.Token.Register(() => TursoBindings.Interrupt(writer.Native));
        try
        {
            if (await WriteLane.RunAsync(() =>
            {
                using var mutation = EnterMutation();
                if (Captures && BackupAnchor is null && Volatile.Read(ref BackupTaps) == 0)
                {
                    TrimUntapped(writer.Connection, null);
                    ObserveCdcSize(writer.Connection);
                }

                var result = Revisions.AfterWrite(writer.Connection);
                ThrowIfUnavailable();
                return result;
            }, refused.Token).ConfigureAwait(false) && Tenant is { } tenant && !IsSystem)
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

    internal async Task TrimIdleAsync(CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        if (Closed || !Captures || Ledger?.Refusal(this) is not null || pool.Paused(this) || !_writer.Wait(0, cancellationToken))
        {
            return;
        }

        TookWriter("idle change history maintenance", null, 0);
        var admitted = false;
        try
        {
            io.Admit();
            admitted = true;
            _ = await io.WithAdmissionAsync(async () => await OnWriterAsync(writer =>
            {
                ObserveCdcSize(writer.Connection);
                var before = RetainedCdcRows;
                if (BackupAnchor is null && Volatile.Read(ref BackupTaps) == 0)
                {
                    TrimUntapped(writer.Connection, null);
                }
                ObserveCdcSize(writer.Connection);
                if (RetainedCdcRows < before)
                {
                    logger?.ZLogInformation($"Idle change history retained {RetainedCdcRows:@RetainedRows} rows");
                }
                return true;
            }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                ReleaseWriter();
            }
            finally
            {
                if (admitted)
                {
                    io.Release();
                }
            }
        }
    }

    internal async Task<RevisionPullResult> PullRevisionAsync(string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfUnavailable();
        using var refused = ReadCancellation(cancellationToken);
        cancellationToken = refused.Token;
        var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
        using var interrupted = Refused.Register(() => TursoBindings.Interrupt(reader.Native));
        using var failed = Ledger?.Refused.Register(() => TursoBindings.Interrupt(reader.Native));
        RevisionPullResult result;
        try
        {
            using var plan = await WithWriterAsync(writer =>
            {
                ThrowIfUnavailable();
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

            ThrowIfUnavailable();
        }
        catch (Exception error)
        {
            interrupted.Dispose();
            failed?.Dispose();
            await RecordOperationDamageAsync(error).ConfigureAwait(false);
            await io.RunAsync(() => pool.Discard(reader), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        interrupted.Dispose();
        failed?.Dispose();
        await ReturnAsync(reader).ConfigureAwait(false);
        ThrowIfUnavailable();
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
        ThrowIfUnavailable();
        if (!await sink.StartAsync(new RevisionPullStart(plan.Revision, plan.Full, plan.DatabaseSize, plan.Count, plan.PageSize) { CdcPosition = cdcPosition, Format = Format }, cancellationToken).ConfigureAwait(false))
        {
            return new RevisionPullResult(plan.Revision, plan.Full, 0, plan.DatabaseSize) { CdcPosition = cdcPosition, Format = Format };
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
                ThrowIfUnavailable();
                await sink.WriteAsync(new RevisionPageBatch(numbers.AsMemory(0, count), pages.AsMemory(0, count * plan.PageSize), plan.PageSize), cancellationToken).ConfigureAwait(false);
                sent += count;
            }

            return new RevisionPullResult(plan.Revision, plan.Full, sent, plan.DatabaseSize) { CdcPosition = cdcPosition, Format = Format };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pages);
            ArrayPool<uint>.Shared.Return(numbers);
        }
    }

    internal async Task<DatabaseRevision> CurrentRevisionAsync(CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }
        ThrowIfUnavailable();
        using var refused = ReadCancellation(cancellationToken);
        cancellationToken = refused.Token;
        var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
        using var interrupted = Refused.Register(() => TursoBindings.Interrupt(reader.Native));
        using var failed = Ledger?.Refused.Register(() => TursoBindings.Interrupt(reader.Native));
        DatabaseRevision result;
        try
        {
            result = await WithWriterAsync(writer =>
            {
                Revisions.Catch(writer.Connection);
                TursoRevisionLedger.BeginSnapshot(reader.Connection);
                try
                {
                    return Revisions.SnapshotRevision(reader.Connection);
                }
                finally
                {
                    TursoRevisionLedger.EndSnapshot(reader.Connection);
                }
            }, "a revision read", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            interrupted.Dispose();
            failed?.Dispose();
            await ReturnAsync(reader).ConfigureAwait(false);
        }
        ThrowIfUnavailable();
        return result;
    }

    internal Task<IReadOnlyList<ReplicaPullObservation>> ReplicaStatusAsync(IReadOnlyList<string> regions, CancellationToken cancellationToken) =>
        WithWriterAsync(writer =>
        {
            ThrowIfUnavailable();
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
