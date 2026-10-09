using System.Net;
using Microsoft.Data.Sqlite;

namespace TerraBase.Fast.Documents;

public sealed class StorageFormatTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(9)]
    public async Task Old_database_formats_are_refused_without_rewriting_data(int version)
    {
        await using var api = await DocumentApi.StartAsync();
        var path = await api.ReplaceFileAsync("old", async path =>
        {
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE old_data (body TEXT); INSERT INTO old_data VALUES ('preserved'); PRAGMA user_version = {version};";
            await command.ExecuteNonQueryAsync();
        });

        var read = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("old")}/collections/items/documents/by-key?id=key");
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.InternalServerError);
        await using var check = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true));
        await check.OpenAsync();
        await using var unchanged = check.CreateCommand();
        unchanged.CommandText = "SELECT body FROM old_data;";
        await Assert.That(await unchanged.ExecuteScalarAsync()).IsEqualTo("preserved");
        unchanged.CommandText = "PRAGMA user_version;";
        await Assert.That(await unchanged.ExecuteScalarAsync()).IsEqualTo((long)version);
    }
}
