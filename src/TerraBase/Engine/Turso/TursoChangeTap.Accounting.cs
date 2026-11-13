using System.Text;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoChangeTap
{
    public async ValueTask<CdcCursor> CursorAsync(CancellationToken cancellationToken) =>
        await AdmittedAsync(async () => await _io.RunAsync(() => new CdcCursor(_anchor,
            (string)Scalar(TursoStatements.Incarnation, 0)!,
            [.. _schema.Select(item => new CdcSchema(item.Key, item.Value.Type, item.Value.Name))]), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<CdcState> StateAsync(CdcCursor? stored, CancellationToken cancellationToken) =>
        await AdmittedAsync(async () => await _io.RunAsync(() =>
        {
            Execute("BEGIN;");
            try
            {
                var position = stored?.Position ?? 0;
                var end = (Scalar("SELECT max(change_id) FROM turso_cdc WHERE change_type = 2;", 0) as long?) ?? 0;
                var continuous = stored is not null
                    && (string)Scalar(TursoStatements.Incarnation, 0)! == stored.Incarnation
                    && (position == 0
                        ? Scalar("SELECT min(change_id) FROM turso_cdc;", 0) is not long first || first == 1
                        : Scalar("SELECT 1 FROM turso_cdc WHERE change_id = $anchor;", position) is not null);
                var oldest = Scalar("SELECT change_time FROM turso_cdc WHERE change_id > $anchor AND change_type = 2 ORDER BY change_id LIMIT 1;", position) as long?;
                return new CdcState(end, continuous, oldest is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
            }
            finally
            {
                Execute("ROLLBACK;");
            }
        }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<bool> ResumeAsync(CdcCursor stored, CancellationToken cancellationToken)
    {
        if (!(await StateAsync(stored, cancellationToken).ConfigureAwait(false)).Continuous)
        {
            return false;
        }

        return await _store.WithWriterAsync(_ =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            _anchor = stored.Position;
            _durableAnchor = stored.Position;
            _schema = stored.Schema.ToDictionary(item => item.RowId, item => (item.Type, item.Name));
            _store.BackupAnchor = _durableAnchor;
            return true;
        }, "the backup's recovered stored cursor", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SkipAsync(long end, CancellationToken cancellationToken)
    {
        await _store.WithWriterAsync(writer =>
        {
            ObjectDisposedException.ThrowIf(_lineage != _store.Lineage, _store);
            _anchor = end;
            return true;
        }, "the backup continuity boundary", cancellationToken).ConfigureAwait(false);
    }

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
