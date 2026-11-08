using System.Net;
using System.Text;
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
        var create = false;
        var trigger = false;
        for (var i = 0; i < dump.Length; i++)
        {
            var character = dump[i];
            text.Append(character);
            if (!quoted && char.IsAsciiLetter(character))
            {
                if (!trigger && Matches(dump, i, "trigger"))
                {
                    create = true;
                }
                else if (create && Matches(dump, i, "begin"))
                {
                    trigger = true;
                }
                else if (trigger && Matches(dump, i, "end") && !char.IsAsciiLetterOrDigit(Next(dump, i + 3)))
                {
                    trigger = false;
                    create = false;
                }

                while (i + 1 < dump.Length && char.IsAsciiLetterOrDigit(dump[i + 1]))
                {
                    i++;
                    text.Append(dump[i]);
                }
            }
            else if (character == '\'')
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
            else if (character == ';' && !quoted && !trigger)
            {
                var statement = text.ToString().Trim();
                if (statement != "BEGIN;" && statement != "COMMIT;" && statement.Length > 0)
                {
                    yield return statement;
                }

                text.Clear();
                create = false;
            }
        }
    }

    private static char Next(string dump, int index) => index < dump.Length ? dump[index] : '\0';

    private static bool Matches(string dump, int index, string word)
    {
        if (index + word.Length > dump.Length)
        {
            return false;
        }

        for (var offset = 0; offset < word.Length; offset++)
        {
            if (char.ToLowerInvariant(dump[index + offset]) != word[offset])
            {
                return false;
            }
        }

        return !char.IsAsciiLetterOrDigit(Next(dump, index + word.Length));
    }

    [Test]
    public async Task An_export_round_trips_into_a_fresh_database(CancellationToken cancellationToken)
    {
        await using var source = await StartSharedAsync();
        var text = "line one\nit's a 'quote'\r\nand a \"double\"";
        await source.OkAsync(
            Stmt("CREATE TABLE items (id INTEGER PRIMARY KEY, body TEXT, data BLOB, score REAL)"),
            Stmt("INSERT INTO items VALUES (1, ?, ?, ?)", text, Blob([0x00, 0x01, 0xFE, 0xFF]), 12.5),
            Stmt("INSERT INTO items VALUES (2, NULL, NULL, NULL)"),
            Stmt("CREATE INDEX items_body ON items (body)"),
            Stmt("CREATE VIEW items_hot AS SELECT id FROM items WHERE score > 0"),
            Stmt("CREATE TRIGGER items_touch AFTER INSERT ON items BEGIN UPDATE items SET body = body WHERE id = NEW.id; END;"));

        using var export = await source.Writer.GetAsync(
            ManagementApi.Uri(source.Database.Data + "/export"), cancellationToken);
        var dump = await export.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(export.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(export.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");

        await using var target = await StartSharedAsync();
        foreach (var batch in Statements(dump).Chunk(256))
        {
            await target.OkAsync([.. batch.Select(sql => Stmt(sql.TrimEnd(';')))]);
        }

        var (textBack, blobBack, realBack, nullBack, indexBack) = await Rows(target);
        await Assert.That(textBack).IsEqualTo(text);
        await Assert.That(blobBack).IsEquivalentTo<byte[], byte>([0, 1, 254, 255]);
        await Assert.That(realBack).IsEqualTo(12.5);
        await Assert.That(nullBack).IsNull();
        await Assert.That(indexBack).IsTrue();
        var schema = await target.OkAsync("SELECT type || ':' || name FROM sqlite_schema WHERE type IN ('view','trigger') ORDER BY name");
        await Assert.That(schema[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("trigger:items_touch");
        await Assert.That(schema[0].GetProperty("rows")[1][0].GetString()).IsEqualTo("view:items_hot");
        return;

        async Task<(string? Text, byte[]? Blob, double? Real, string? Null, bool Index)> Rows(SqlNativeDatabase database)
        {
            var rows = await database.OkAsync("SELECT body, data, score FROM items WHERE id = 1");
            var nulls = await database.OkAsync("SELECT body FROM items WHERE id = 2");
            var indexes = await database.OkAsync("SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'items_body'");
            return (rows[0].GetProperty("rows")[0][0].GetString(), rows[0].GetProperty("rows")[0][1].GetProperty("base64").GetString() is { } encoded
                    ? Convert.FromBase64String(encoded) : null,
                rows[0].GetProperty("rows")[0][2].GetDouble(), nulls[0].GetProperty("rows")[0][0].GetString(),
                indexes[0].GetProperty("rows")[0][0].GetInt64() == 1);
        }
    }

    [Test]
    public async Task An_export_refuses_a_key_narrowed_to_another_database(CancellationToken cancellationToken)
    {
        await using var source = await StartSharedAsync();
        var other = await source.Api.CreateManagedDatabaseAsync(source.Database.Organization, source.Database.Project, "other");
        var key = await source.Api.Server.IssueNarrowedAsync(other.Organization, other.Project, other.Id, "sql:read");
        using var client = source.Api.DataClient(key);
        var (sqlStatus, _) = await source.PostAsync(client, "SELECT 1");
        using var export = await client.GetAsync(ManagementApi.Uri(source.Database.Data + "/export"), cancellationToken);
        await Assert.That(sqlStatus).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(export.StatusCode).IsEqualTo(sqlStatus);
    }
}
