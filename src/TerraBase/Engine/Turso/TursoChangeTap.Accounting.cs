using System.Text;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoChangeTap
{
    public async ValueTask<CdcCursor> CursorAsync(CancellationToken cancellationToken) =>
        await AdmittedAsync(async () => await _io.RunAsync(() => new CdcCursor(_anchor,
            (string)Scalar(TursoStatements.Incarnation, 0)!,
            [.. _schema.Select(item => new CdcSchema(item.Key, item.Value.Type, item.Value.Name))]), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<CdcRetained> RetainedAsync(long stored, CancellationToken cancellationToken) =>
        await AdmittedAsync(async () => await _io.RunAsync(() =>
        {
            Execute("BEGIN;");
            try
            {
                var anchor = Scalar("SELECT min(change_id) FROM turso_cdc;", 0) is long first && first > 1 ? first : 0;
                return new CdcRetained((string)Scalar(TursoStatements.Incarnation, 0)!, anchor,
                    Time(Scalar("SELECT change_time FROM turso_cdc WHERE change_id = $anchor;", anchor)),
                    Time(Scalar("SELECT change_time FROM turso_cdc WHERE change_id > $anchor AND change_type = 2 ORDER BY change_id LIMIT 1;", stored)));
            }
            finally
            {
                Execute("ROLLBACK;");
            }
        }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<long> CommittedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
        await using var transaction = await _store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        return await ((TursoStorageTransaction)transaction).ReadShapeHeadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResumeAsync(CdcCursor stored, CancellationToken cancellationToken)
    {
        await _store.WithWriterAsync(writer =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT coalesce(min(change_id), 0) FROM turso_cdc;";
            var first = (long)command.ExecuteScalar()!;
            if (first > 1 && first > stored.Position)
            {
                throw new InvalidOperationException("the backup's stored cursor is older than the retained change log");
            }

            _anchor = stored.Position;
            _durableAnchor = stored.Position;
            Volatile.Write(ref _storedAnchor, stored.Position);
            _schema = stored.Schema.ToDictionary(item => item.RowId, item => (item.Type, item.Name));
            _store.BackupAnchor = _durableAnchor;
            return true;
        }, "the backup's recovered stored cursor", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DiscardAsync(CancellationToken cancellationToken)
    {
        await _store.WithWriterAsync(writer =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            using (var newest = writer.Connection.CreateCommand())
            {
                newest.CommandText = TursoStatements.NewestChange;
                _durableAnchor = Math.Max(_durableAnchor, (long)newest.ExecuteScalar()!);
            }

            _store.BackupAnchor = _durableAnchor;
            return Truncate(writer);
        }, "the backup's discarded change log", cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? Time(object? seconds) => seconds is long value ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    public async ValueTask SaveAccountingAsync(string id, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!_store.IsSystem)
        {
            throw new InvalidOperationException("backup accounting belongs to the system database");
        }

        await _store.WithWriterAsync(writer =>
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = TursoSession.CaptureOff;
            command.ExecuteNonQuery();
            try
            {
                command.CommandText = """
                    INSERT INTO _tb_records(kind, id, version, body) VALUES ('backup_continuity', $id, 1, $body)
                    ON CONFLICT(kind, id) DO UPDATE SET version = version + 1, body = excluded.body;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$body", Encoding.UTF8.GetString(body.Span));
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Parameters.Clear();
                command.CommandText = TursoSession.CaptureOn;
                command.ExecuteNonQuery();
            }

            return true;
        }, "the backup's durable position accounting", cancellationToken).ConfigureAwait(false);
    }
}
