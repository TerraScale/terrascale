using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>Only master-wrapped data keys reach disk; unwrapping stays outside the blocking I/O workers.</summary>
internal sealed class DatabaseKeys(IMasterKey master)
{
    internal const string Suffix = ".key";
    internal const int MaxFileBytes = 16 * 1024;
    private const string Header = "TERRABASE-DB-KEY-1\n";

    internal async Task<string> CreateAsync(string path, TursoRunner io, CancellationToken cancellationToken)
    {
        var (key, wrapped) = await master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateKey(key);
            var record = Encoding.UTF8.GetBytes(Header + wrapped + "\n");
            ValidateRecord(record);
            await io.RunAsync(() =>
            {
                RejectLink(path);
                RejectLink(path + Suffix);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new IOException("a database key create target already contains files");
                }

                using var file = new FileStream(path + Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(record);
                file.Flush(flushToDisk: true);
            }, cancellationToken).ConfigureAwait(false);
            return ConnectionString(path, key, SqliteOpenMode.ReadWriteCreate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal async Task<string> ExistingAsync(string path, TursoRunner io, CancellationToken cancellationToken)
    {
        var wrapped = await io.RunAsync(() =>
        {
            RejectLink(path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("the encrypted database file is missing");
            }

            return ReadWrapped(path);
        }, cancellationToken).ConfigureAwait(false);
        var key = await master.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateKey(key);
            return ConnectionString(path, key, SqliteOpenMode.ReadWrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[] ReadRecord(string path)
    {
        RejectLink(path + Suffix);
        try
        {
            using var file = new FileStream(path + Suffix, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaxFileBytes)
            {
                throw new InvalidDataException("the database wrapped key record is malformed");
            }

            var record = new byte[checked((int)file.Length)];
            file.ReadExactly(record);
            if (file.ReadByte() != -1)
            {
                throw new InvalidDataException("the database wrapped key record is malformed");
            }

            ValidateRecord(record);
            return record;
        }
        catch (FileNotFoundException)
        {
            throw new InvalidDataException("the database wrapped key record is missing");
        }
    }

    private static string ReadWrapped(string path)
    {
        var record = ReadRecord(path);
        return Encoding.ASCII.GetString(record.AsSpan(Header.Length, record.Length - Header.Length - 1));
    }

    internal static void ValidateRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length <= Header.Length + 1 || record.Length > MaxFileBytes
            || !record.StartsWith("TERRABASE-DB-KEY-1\n"u8) || record[^1] != (byte)'\n')
        {
            throw new InvalidDataException("the database wrapped key record is malformed");
        }

        foreach (var value in record[Header.Length..^1])
        {
            if (value is < 33 or > 126)
            {
                throw new InvalidDataException("the database wrapped key record is malformed");
            }
        }
    }

    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null
            || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("database key paths must not contain symbolic links");
        }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new InvalidDataException("the database data key must contain 256 bits");
        }
    }

    private static string ConnectionString(string path, byte[] key, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        DefaultTimeout = 30,
        EncryptionCipher = "aegis256",
        // The native provider takes a hex string; never include a connection string in diagnostics.
        EncryptionKey = Convert.ToHexString(key),
    }.ToString();
}
