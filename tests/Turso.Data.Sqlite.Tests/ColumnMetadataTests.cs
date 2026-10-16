namespace Turso.Data.Sqlite.Tests;

/// <summary>Native column metadata preserves typed values without repeated schema queries.</summary>
public sealed class ColumnMetadataTests
{
    /// <summary>Table columns retain their declared types and GUID conversion for both native encodings.</summary>
    [Test]
    public async Task DeclaredColumnTypesPreserveValues()
    {
        using var database = new TemporaryDatabase();
        var guid = Guid.NewGuid();
        using var write = database.Connection.CreateCommand();
        write.CommandText = "CREATE TABLE typed (id INTEGER PRIMARY KEY, text_id GUID, blob_id GUID, text_value VARCHAR(40), blob_value BLOB, integer_value INTEGER, real_value REAL, null_value TEXT);";
        write.ExecuteNonQuery();
        write.CommandText = "INSERT INTO typed VALUES (1, $textId, $blobId, 'text', X'0102', 7, 2.5, NULL);";
        write.Parameters.AddWithValue("$textId", guid.ToString());
        write.Parameters.AddWithValue("$blobId", guid.ToByteArray());
        write.ExecuteNonQuery();

        using (var native = Turso.Raw.Public.TursoBindings.PrepareStatement(database.Connection.DatabaseHandle, "SELECT text_id, blob_id, text_value FROM typed;"))
        {
            await Assert.That(Turso.Raw.Public.TursoBindings.Read(native)).IsTrue();
            await Assert.That(Turso.Raw.Public.TursoBindings.GetDeclaredTypeName(native, 0)).IsEqualTo("GUID");
            await Assert.That(Turso.Raw.Public.TursoBindings.GetDeclaredTypeName(native, 1)).IsEqualTo("GUID");
            await Assert.That(Turso.Raw.Public.TursoBindings.GetDeclaredTypeName(native, 2)).IsEqualTo("VARCHAR");
        }

        using var query = database.Connection.CreateCommand();
        query.CommandText = "SELECT text_id, blob_id, text_value, blob_value, integer_value, real_value, null_value FROM typed;";
        using var reader = query.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetValue(0)).IsEqualTo(guid);
        await Assert.That(reader.GetValue(1)).IsEqualTo(guid);
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(Guid));
        await Assert.That(reader.GetFieldType(1)).IsEqualTo(typeof(Guid));
        await Assert.That(reader.GetDataTypeName(2)).IsEqualTo("VARCHAR");
        await Assert.That(reader.GetValue(2)).IsEqualTo("text");
        await Assert.That((byte[])reader.GetValue(3)).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(reader.GetFieldType(3)).IsEqualTo(typeof(byte[]));
        await Assert.That(reader.GetValue(4)).IsEqualTo(7L);
        await Assert.That(reader.GetFieldType(4)).IsEqualTo(typeof(long));
        await Assert.That(reader.GetValue(5)).IsEqualTo(2.5);
        await Assert.That(reader.GetValue(6)).IsEqualTo(DBNull.Value);
    }

    /// <summary>A joined column uses its own declaration instead of a same-named column in the first table.</summary>
    [Test]
    public async Task JoinedGuidUsesItsSourceDeclaration()
    {
        using var database = new TemporaryDatabase();
        var guid = Guid.NewGuid();
        using var write = database.Connection.CreateCommand();
        write.CommandText = "CREATE TABLE a (id INTEGER PRIMARY KEY, g GUID); CREATE TABLE b (id INTEGER PRIMARY KEY, g UNIQUEIDENTIFIER); INSERT INTO a VALUES (1, $guid); INSERT INTO b VALUES (1, $guid);";
        write.Parameters.AddWithValue("$guid", guid.ToString());
        write.ExecuteNonQuery();

        using var query = database.Connection.CreateCommand();
        query.CommandText = "SELECT b.g FROM a JOIN b ON a.id = b.id;";
        using var reader = query.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetDataTypeName(0)).IsEqualTo("UNIQUEIDENTIFIER");
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(Guid));
        await Assert.That(reader.GetValue(0)).IsEqualTo(guid);
    }

    /// <summary>View metadata reflects the affinities returned by the pinned native library.</summary>
    [Test]
    public async Task ViewColumnsUseNativeAffinity()
    {
        using var database = new TemporaryDatabase();
        var guid = Guid.NewGuid();
        using var write = database.Connection.CreateCommand();
        write.CommandText = "CREATE TABLE typed (g GUID, label VARCHAR(40)); INSERT INTO typed VALUES ($guid, 'text'); CREATE VIEW v AS SELECT g, label FROM typed;";
        write.Parameters.AddWithValue("$guid", guid.ToString());
        write.ExecuteNonQuery();

        using var query = database.Connection.CreateCommand();
        query.CommandText = "SELECT * FROM v;";
        using var reader = query.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        // Turso v0.8.2 returns affinity for views, so the GUID remains a string.
        await Assert.That(reader.GetDataTypeName(0)).IsEqualTo("NUMERIC");
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(string));
        await Assert.That(reader.GetValue(0)).IsEqualTo(guid.ToString());
        await Assert.That(reader.GetDataTypeName(1)).IsEqualTo("TEXT");
        await Assert.That(reader.GetValue(1)).IsEqualTo("text");
    }

    /// <summary>Expression metadata falls back to its runtime value type rather than a neighbouring base column.</summary>
    [Test]
    public async Task ExpressionsKeepTheirRuntimeTypesAcrossResultSets()
    {
        using var database = new TemporaryDatabase();
        using var query = database.Connection.CreateCommand();
        query.CommandText = "SELECT id || '-suffix' AS combined, 42 AS integer_value, X'0102' AS bytes FROM t; SELECT 2.5 AS real_value;";
        using var reader = query.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetValue(0)).IsEqualTo("a-suffix");
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(string));
        await Assert.That(reader.GetValue(1)).IsEqualTo(42L);
        await Assert.That((byte[])reader.GetValue(2)).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(reader.NextResult()).IsTrue();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetValue(0)).IsEqualTo(2.5);
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(double));
    }
}
