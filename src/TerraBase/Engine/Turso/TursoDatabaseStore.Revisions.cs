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
            LogAfterCommit("the revision index", error);
        }
    }

    internal async Task<RevisionPullResult> PullRevisionAsync(string? clientRevision, Action<uint, ReadOnlySpan<byte>> page, CancellationToken cancellationToken)
    {
        while (true)
        {
            await WithWriterAsync(writer =>
            {
                Revisions.Catch(writer.Connection);
                return true;
            }, "a revision pull", cancellationToken).ConfigureAwait(false);

            var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
            RevisionPullResult? result;
            try
            {
                result = await reader.ExecuteAsync(io, () => Revisions.Pull(reader.Connection, clientRevision, page), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
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
