using TerraBase.Engine.Revisions;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private async ValueTask IndexRevisionAsync()
    {
        if (_writerSession is not { } writer || Closed)
        {
            return;
        }

        try
        {
            await WriteLane.RunAsync(() => Revisions.AfterWrite(writer.Connection), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await RecordOperationDamageAsync(error).ConfigureAwait(false);
            LogAfterCommit("the revision index", error);
        }
    }

    internal async Task<RevisionPullResult> PullRevisionAsync(string? clientRevision, Action<uint, ReadOnlySpan<byte>> page, CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfRefused(rebuild: false);
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
                result = await io.RunAsync(() => TursoRevisionLedger.Stream(reader.Connection, plan, page, cancellationToken), cancellationToken).ConfigureAwait(false);
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

    private void RefuseFaultedCopy()
    {
        if (CloseRefusal is { } refusal)
        {
            throw new ReplicaCopyFaultedException(refusal);
        }
    }

    internal Task StartIncarnationAsync(CancellationToken cancellationToken) =>
        WithWriterAsync(writer =>
        {
            Revisions.StartIncarnation();
            Revisions.Attach(writer.Connection);
            return true;
        }, "a new revision incarnation", cancellationToken);
}
