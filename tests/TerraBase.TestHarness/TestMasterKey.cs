using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

internal sealed class TestMasterKey : IMasterKey
{
    private static readonly MasterKey Master = new(Material());

    private static byte[] Material() => SHA256.HashData("TerraBase integration fixtures only; never a production master"u8);

    public Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken) =>
        Master.GenerateDataKeyAsync(cancellationToken);

    public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken) =>
        Master.UnwrapAsync(wrapped, cancellationToken);

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

    internal static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false });

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
