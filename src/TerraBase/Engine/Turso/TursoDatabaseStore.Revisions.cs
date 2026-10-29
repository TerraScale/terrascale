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
        while (true)
        {
            var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
            RevisionPullResult? result;
            try
            {
                await WithWriterAsync(writer =>
                {
                    Revisions.Catch(writer.Connection);
                    TursoRevisionLedger.BeginSnapshot(reader.Connection);
                    return true;
                }, "a revision pull", cancellationToken).ConfigureAwait(false);
                try
                {
                    result = await io.RunAsync(() => Revisions.Pull(reader.Connection, clientRevision, page, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await io.RunAsync(() => TursoRevisionLedger.EndSnapshot(reader.Connection), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                await RecordOperationDamageAsync(error).ConfigureAwait(false);
                await io.RunAsync(() => pool.Discard(reader), CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await ReturnAsync(reader).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
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
