using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Journey.Keyring;

internal static class InventoryProof
{
    private const string CatalogSql = "SELECT id, key_id, state FROM databases ORDER BY id";

    private const string DeletedState = "deleted";

    internal static void Write(DataLayout layout, MasterKeys keys, string output)
    {
        var systemKeyId = layout.SystemKeyId;
        var systemOpener = Opener(layout.DatabaseFile(Ulid.Empty), keys, Ulid.Empty);
        Require(systemOpener == systemKeyId, string.Concat("the system database opens under '", systemOpener, "' but system-key-id records '", systemKeyId, "'"));
        var local = layout.LocalDatabases().ToHashSet();
        List<InventoryEntry> entries = [];
        List<string> shredded = [];
        foreach (var row in Catalog(layout, keys, systemKeyId))
        {
            if (row is not [{ } id, { } keyId, { } state] || !Ulid.TryParse(id, out var databaseId))
            {
                throw new InvalidOperationException("the catalog holds a malformed database row");
            }

            if (state == DeletedState)
            {
                Require(!Directory.Exists(layout.DatabaseDirectory(databaseId)), string.Concat("deleted database ", id, " left local files behind"));
                shredded.Add(id);
                continue;
            }

            Require(local.Remove(databaseId), string.Concat("catalog database ", id, " has no local file"));
            var opener = Opener(layout.DatabaseFile(databaseId), keys, databaseId);
            Require(opener == keyId, string.Concat("database ", id, " opens under '", opener, "' but the catalog records '", keyId, "'"));
            var snapshots = layout.Snapshots(databaseId);
            foreach (var snapshot in snapshots)
            {
                Require(Opener(snapshot, keys, databaseId) == keyId, string.Concat("a snapshot of ", id, " is not under its database key"));
            }

            entries.Add(new InventoryEntry(id, keyId, state, snapshots.Count));
        }

        local.Remove(Ulid.Empty);
        Require(local.Count == 0, string.Concat("local databases are missing from the catalog: ", string.Join(", ", local)));
        var inventory = new Inventory(keys.Active, systemKeyId, entries, shredded);
        File.WriteAllText(output, JsonSerializer.Serialize(inventory, ProofJsonContext.Default.Inventory));
        var counts = keys.Ids.Select(keyId => string.Concat(keyId, "=", entries.Count(entry => entry.KeyId == keyId).ToString(CultureInfo.InvariantCulture)));
        Console.WriteLine(string.Concat("keyring-driver:inventory system=", systemKeyId, " databases=", entries.Count.ToString(CultureInfo.InvariantCulture),
            " ", string.Join(' ', counts), " shredded=", shredded.Count.ToString(CultureInfo.InvariantCulture)));
    }

    internal static string Opener(string path, MasterKeys keys, Ulid databaseId)
    {
        List<string> opening = [];
        foreach (var keyId in keys.Ids)
        {
            var rawKey = keys.Derive(databaseId, keyId);
            var opens = EncryptedFile.Open(path, rawKey) switch
            {
                EncryptedFile file => Close(file),
                Refused => false,
            };
            CryptographicOperations.ZeroMemory(rawKey);
            if (opens)
            {
                opening.Add(keyId);
            }
        }

        return opening is [var only]
            ? only
            : throw new InvalidOperationException(string.Concat(Path.GetFileName(path), " opens under ", opening.Count.ToString(CultureInfo.InvariantCulture),
                " configured master keys instead of exactly one"));
    }

    private static IReadOnlyList<IReadOnlyList<string?>> Catalog(DataLayout layout, MasterKeys keys, string systemKeyId)
    {
        var rawKey = keys.Derive(Ulid.Empty, systemKeyId);
        var opened = EncryptedFile.Open(layout.DatabaseFile(Ulid.Empty), rawKey);
        CryptographicOperations.ZeroMemory(rawKey);
        return opened switch
        {
            EncryptedFile file => Read(file),
            Refused refused => throw new InvalidOperationException(string.Concat("the system database refused its recorded key: ", refused.Message)),
        };
    }

    private static IReadOnlyList<IReadOnlyList<string?>> Read(EncryptedFile file)
    {
        using (file)
        {
            return file.Query(CatalogSql);
        }
    }

    private static bool Close(EncryptedFile file)
    {
        file.Dispose();
        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
