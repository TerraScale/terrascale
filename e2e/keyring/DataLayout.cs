using TerraBase.Configuration;

namespace Journey.Keyring;

internal sealed class DataLayout(TerraBaseOptions options)
{
    private const string SystemKeyIdFile = "system-key-id";

    private const string SnapshotDirectory = "snapshots";

    public string DataDirectory => options.DataDirectory;

    public string? ConfiguredActiveKeyId => options.DatabaseKeyring?.Active;

    public string SystemKeyId => File.ReadAllText(Path.Combine(options.DataDirectory, SystemKeyIdFile)).Trim();

    public string DatabaseDirectory(Ulid databaseId) => Path.Combine(options.StorageDirectory, databaseId.ToString());

    public string DatabaseFile(Ulid databaseId) => Path.Combine(DatabaseDirectory(databaseId), string.Concat(databaseId.ToString(), ".db"));

    public IReadOnlyList<string> Snapshots(Ulid databaseId)
    {
        var directory = Path.Combine(DatabaseDirectory(databaseId), SnapshotDirectory);
        return Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "*.db").Order(StringComparer.Ordinal)] : [];
    }

    public IReadOnlyList<Ulid> LocalDatabases()
    {
        List<Ulid> databases = [];
        foreach (var directory in Directory.GetDirectories(options.StorageDirectory))
        {
            if (Ulid.TryParse(Path.GetFileName(directory), out var databaseId) && File.Exists(DatabaseFile(databaseId)))
            {
                databases.Add(databaseId);
            }
        }

        return [.. databases.Order()];
    }
}
