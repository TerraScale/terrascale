using System.Security.Cryptography;
using TerraBase.Configuration;

namespace TerraBase.Engine;

public sealed record DatabaseKey(string KeyId, byte[] RawKey);

internal sealed class DatabaseKeyring
{
    private const string DevelopmentKeyFile = "development-master.key";

    private static readonly byte[] Info = "terrabase/v2/database-key"u8.ToArray();

    private readonly KeyringSettings _settings;

    private DatabaseKeyring(KeyringSettings settings)
    {
        _settings = settings;
    }

    public string ActiveKeyId => _settings.Active;

    public IEnumerable<string> KeyIds => _settings.Keys.Keys;

    public static Result<DatabaseKeyring, ConfigurationError> Create(TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DatabaseKeyring is { } configured)
        {
            return new DatabaseKeyring(configured);
        }

        if (!options.IsDevelopment)
        {
            return new ConfigurationError("database master keys are required outside Development");
        }

        return DevelopmentKey(options.DataDirectory) is { } key
            ? new DatabaseKeyring(KeyringSettings.Single(key))
            : new ConfigurationError("the Development master key under data-directory is unreadable");
    }

    public Result<DatabaseKey, NotFound> Derive(Ulid databaseId, string keyId)
    {
        return _settings.Keys.TryGetValue(keyId, out var master)
            ? Derive(databaseId, keyId, master)
            : new NotFound(string.Concat("master key '", keyId, "' is not configured"), "unknown_master_key");
    }

    private static DatabaseKey Derive(Ulid databaseId, string keyId, byte[] master)
    {
        var salt = databaseId.ToByteArray();
        var raw = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, DatabaseOpenRequest.RawKeyLength, salt, Info);
        return new DatabaseKey(keyId, raw);
    }

    private static byte[]? DevelopmentKey(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, DevelopmentKeyFile);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                return existing.Length == 32 ? existing : null;
            }

            var created = RandomNumberGenerator.GetBytes(32);
            var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, streamOptions);
            stream.Write(created);
            stream.Flush(flushToDisk: true);
            return created;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
