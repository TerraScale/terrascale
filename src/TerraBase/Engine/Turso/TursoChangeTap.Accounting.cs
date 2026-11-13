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
                using var command = _reader.CreateCommand();
                var start = TursoStorageTransaction.HistoryStart(command) ?? throw new InvalidDataException("the change log has no recorded start");
                var oldest = Scalar("SELECT min(change_id) FROM turso_cdc;", 0) as long?;
                var newest = Scalar("SELECT max(change_id) FROM turso_cdc;", 0) as long? ?? 0;
                var first = Math.Max(start, oldest ?? start);
                var anchor = stored > newest ? stored + 1 : first > 1 ? first : 0;
                return new CdcRetained((string)Scalar(TursoStatements.Incarnation, 0)!, anchor, start, oldest,
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
            var start = TursoStorageTransaction.HistoryStart(command) ?? throw new InvalidDataException("the change log has no recorded start");
            command.CommandText = TursoStatements.NewestChange;
            if (start > 1 && start > stored.Position || stored.Position > (long)command.ExecuteScalar()!)
            {
                throw new InvalidOperationException("the backup's stored cursor is outside the retained change log");
            }

            _anchor = stored.Position;
            _durableAnchor = stored.Position;
            Volatile.Write(ref _storedAnchor, stored.Position);
            _schema = stored.Schema.ToDictionary(item => item.RowId, item => (item.Type, item.Name));
            _store.BackupAnchor = _durableAnchor;
            return true;
        }, "the backup's recovered stored cursor", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CdcHistory> DiscardAsync(CancellationToken cancellationToken) =>
        await _store.WithWriterAsync(writer =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            using var command = writer.Connection.CreateCommand();
            command.CommandText = TursoStatements.NewestChange;
            _durableAnchor = Math.Max(_durableAnchor, (long)command.ExecuteScalar()!);
            _store.BackupAnchor = _durableAnchor;
            _ = Truncate(writer);
            var start = TursoStorageTransaction.HistoryStart(command) ?? throw new InvalidDataException("the change log has no recorded start");
            command.CommandText = "SELECT min(change_id) FROM turso_cdc;";
            return new CdcHistory(start, command.ExecuteScalar() as long?);
        }, "the backup's discarded change log", cancellationToken).ConfigureAwait(false);

    private static bool StartHistory(TursoSession writer)
    {
        using var command = writer.Connection.CreateCommand();
        if (TursoStorageTransaction.HistoryStart(command) is null)
        {
            command.CommandText = "SELECT min(change_id) FROM turso_cdc;";
            _ = TursoStorageTransaction.RecordHistoryStart(command, command.ExecuteScalar() as long? ?? 1);
        }

        return true;
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
