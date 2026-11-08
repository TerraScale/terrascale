using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class ExportDatabaseTests
{
    private static object Stmt(string sql, params object?[] args) => new { sql, args };

    private static object Blob(byte[] bytes) => new { base64 = Convert.ToBase64String(bytes) };

    private static IEnumerable<string> Statements(string dump)
    {
        var text = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < dump.Length; i++)
        {
            var character = dump[i];
            text.Append(character);
            if (character == '\'')
            {
                if (quoted && i + 1 < dump.Length && dump[i + 1] == '\'')
                {
                    text.Append('\'');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ';' && !quoted)
            {
                var statement = text.ToString().Trim();
                if (statement != "BEGIN;" && statement != "COMMIT;" && statement.Length > 0)
                {
                    yield return statement;
                }

                text.Clear();
            }
        }
    }

    [Test]
    public async Task An_export_round_trips_into_a_fresh_database(CancellationToken cancellationToken)
    {
        await using var source = await StartSharedAsync();
        var text = "line one\nit's a 'quote'\r\nand a \"double\"";
        await source.OkAsync(
            "CREATE TABLE items (id INTEGER PRIMARY KEY, body TEXT, data BLOB, score REAL)",
            Stmt("INSERT INTO items VALUES (1, ?, ?, ?)", text, Blob([0x00, 0x01, 0xFE, 0xFF]), 12.5),
            "INSERT INTO items VALUES (2, NULL, NULL, NULL)",
            "CREATE INDEX items_body ON items (body)");

        using var export = await source.Writer.GetAsync(
            ManagementApi.Uri(source.Database.Data + "/export"), cancellationToken);
        var dump = await export.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(export.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(export.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");

        var restore = await source.Api.CreateManagedDatabaseAsync();
        foreach (var batch in Statements(dump).Chunk(256))
        {
            using var run = await source.Writer.PostAsJsonAsync(
                ManagementApi.Uri(restore.Data + "/sql/execute"),
                new { statements = batch.Select(sql => Stmt(sql.TrimEnd(';'))).ToArray() }, cancellationToken);
            var body = JsonElement.Parse(await run.Content.ReadAsStringAsync(cancellationToken));
            await Assert.That(run.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        }

        var (textBack, blobBack, realBack, nullBack, indexBack) = await Rows(restore);
        await Assert.That(textBack).IsEqualTo(text);
        await Assert.That(blobBack).IsEquivalentTo<byte[], byte>([0, 1, 254, 255]);
        await Assert.That(realBack).IsEqualTo(12.5);
        await Assert.That(nullBack).IsNull();
        await Assert.That(indexBack).IsTrue();
        return;

        async Task<(string? Text, byte[]? Blob, double? Real, string? Null, bool Index)> Rows(ManagedDatabaseRef database)
        {
            var rows = await ManagementApi.RowsAsync(source.Writer, database.Data,
                "SELECT body, data, score FROM items WHERE id = 1", cancellationToken);
            var nulls = await ManagementApi.RowsAsync(source.Writer, database.Data,
                "SELECT body FROM items WHERE id = 2", cancellationToken);
            var indexes = await ManagementApi.RowsAsync(source.Writer, database.Data,
                "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'items_body'", cancellationToken);
            return (rows[0][0].GetString(), rows[0][1].GetProperty("base64").GetString() is { } encoded
                    ? Convert.FromBase64String(encoded) : null,
                rows[0][2].GetDouble(), nulls[0][0].GetString(), indexes[0][0].GetInt64() == 1);
        }
    }
}
