using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

/// <summary>Synthetic test-only envelope master, stable across fixture process restarts.</summary>
internal sealed class TestMasterKey : IMasterKey
{
    private static readonly MasterKey Master = new(Material());

    private static byte[] Material() => SHA256.HashData("TerraBase integration fixtures only; never a production master"u8);

    /// <summary>Configures a real child binary with a synthetic master, without keys in argv or files.</summary>
    internal static void ConfigureProcess(ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("AWS_", StringComparison.Ordinal)
            || name == "BUCKET_NAME" || name.StartsWith("TERRABASE_MASTER_KEY", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        var material = Material();
        try
        {
            start.Environment["TERRABASE_MASTER_KEY"] = Convert.ToBase64String(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken) =>
        Master.GenerateDataKeyAsync(cancellationToken);

    public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken) =>
        Master.UnwrapAsync(wrapped, cancellationToken);

    /// <summary>Seeds a wrapped-only key for a new fixture file, never for an existing database.</summary>
    internal static void Create(string path)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException("a fixture key cannot replace an existing database key");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var file = new FileStream(path + DatabaseKeys.Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(Encoding.ASCII.GetBytes("TERRABASE-DB-KEY-1\n" + Master.Wrap(key) + "\n"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens an encrypted fixture with its existing wrapped key; missing keys always fail.</summary>
    internal static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false });

    /// <summary>Applies encryption while retaining a fixture's connection options.</summary>
    internal static string ConnectionString(SqliteConnectionStringBuilder builder)
    {
        var record = DatabaseKeys.ReadRecord(builder.DataSource);
        var wrapped = Encoding.ASCII.GetString(record).Split('\n')[1];
        var key = Master.Unwrap(wrapped);
        try
        {
            builder.EncryptionCipher = "aegis256";
            builder.EncryptionKey = Convert.ToHexString(key);
            return builder.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
