using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Journey.Keyring;

internal static class FileEvidence
{
    private const string RowSql = "SELECT value, marker FROM keyring_rows";

    private const int MinimumMarkerBytes = 16;

    private static readonly byte[] PlaintextHeader = "SQLite format 3\0"u8.ToArray();

    internal static void Prove(DataLayout layout, MasterKeys keys, EvidenceRequest request)
    {
        Require(Ulid.TryParse(request.Database, out var databaseId), "the evidence request names no database ULID");
        var marker = Encoding.UTF8.GetBytes(request.Marker);
        Require(marker.Length >= MinimumMarkerBytes, "the evidence marker is too short to be unique");
        var path = layout.DatabaseFile(databaseId);
        Require(File.Exists(path), string.Concat("database ", request.Database, " has no local file"));

        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(layout.DataDirectory, "*", SearchOption.AllDirectories))
        {
            Require(File.ReadAllBytes(file).AsSpan().IndexOf(marker) < 0, string.Concat("plaintext row bytes are at rest in ", Path.GetRelativePath(layout.DataDirectory, file)));
            scanned++;
        }

        var databaseFiles = Directory.GetFiles(layout.DatabaseDirectory(databaseId), "*", SearchOption.AllDirectories);
        List<string> pages = [path, .. layout.Snapshots(databaseId)];
        foreach (var page in pages)
        {
            Require(!File.ReadAllBytes(page).AsSpan().StartsWith(PlaintextHeader), string.Concat(Path.GetFileName(page), " carries a plaintext SQLite header"));
        }

        Require(InventoryProof.Opener(path, keys, databaseId) == request.KeyId, string.Concat("database ", request.Database, " is not under master key '", request.KeyId, "'"));
        var rawKey = keys.Derive(databaseId, request.KeyId);
        var rows = EncryptedFile.Open(path, rawKey) switch
        {
            EncryptedFile file => Rows(file),
            Refused refused => throw new InvalidOperationException(string.Concat("the derived key did not open ", request.Database, ": ", refused.Message)),
        };
        Require(rows is [[var value, var text]] && value == "42" && text == request.Marker, "the derived key did not read row 42 with its marker");
        foreach (var snapshot in layout.Snapshots(databaseId))
        {
            Require(Opens(snapshot, rawKey), string.Concat("snapshot ", Path.GetFileName(snapshot), " does not open under the database key"));
        }

        var wrong = RandomNumberGenerator.GetBytes(MasterKeys.RawKeyLength);
        Require(!Opens(path, wrong), "a random key opened the database");
        rawKey[0] ^= 1;
        Require(!Opens(path, rawKey), "a key one bit away opened the database");
        CryptographicOperations.ZeroMemory(rawKey);
        Console.WriteLine(string.Concat("keyring-driver:evidence database=", request.Database, " key=", request.KeyId,
            " database-files=", databaseFiles.Length.ToString(CultureInfo.InvariantCulture),
            " scanned=", scanned.ToString(CultureInfo.InvariantCulture), " plaintext=absent header=encrypted other-keys=refused row=42"));
    }

    private static bool Opens(string path, byte[] rawKey) => EncryptedFile.Open(path, rawKey) switch
    {
        EncryptedFile file => Close(file),
        Refused => false,
    };

    private static IReadOnlyList<IReadOnlyList<string?>> Rows(EncryptedFile file)
    {
        using (file)
        {
            return file.Query(RowSql);
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
