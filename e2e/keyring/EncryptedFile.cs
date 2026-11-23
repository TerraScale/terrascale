using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Journey.Keyring;

internal sealed record Refused(int Code, string Message);

internal readonly union Opened(EncryptedFile, Refused);

internal sealed partial class EncryptedFile : IDisposable
{
    private const string Library = "sqlite3mc";

    private const int OpenReadOnly = 0x00000001;

    private const int OpenUri = 0x00000040;

    private const int Ok = 0;

    private const int Row = 100;

    private const int Done = 101;

    private static readonly byte[] KeyPrefix = "raw:bin:"u8.ToArray();

    private nint _db;

    private EncryptedFile(nint db)
    {
        _db = db;
    }

    public static void UseServiceNatives() => NativeLibrary.SetDllImportResolver(typeof(EncryptedFile).Assembly, Resolve);

    public static Opened Open(string path, ReadOnlySpan<byte> rawKey)
    {
        var code = OpenV2(Immutable(path), out var db, OpenReadOnly | OpenUri, 0);
        if (code != Ok)
        {
            var message = db == 0 ? Text(ErrorString(code)) : Text(ErrorMessage(db));
            _ = CloseV2(db);
            return new Refused(code, message);
        }

        var file = new EncryptedFile(db);
        byte[] material = [.. KeyPrefix, .. rawKey];
        var refused = file.Run("PRAGMA cipher = 'chacha20';")
            ?? file.Key(material)
            ?? file.Run("SELECT count(*) FROM sqlite_schema;");
        CryptographicOperations.ZeroMemory(material);
        if (refused is not null)
        {
            file.Dispose();
            return refused;
        }

        return file;
    }

    public IReadOnlyList<IReadOnlyList<string?>> Query(string sql)
    {
        ObjectDisposedException.ThrowIf(_db == 0, this);
        var code = PrepareV2(_db, sql, -1, out var statement, 0);
        if (code != Ok)
        {
            throw new InvalidOperationException(string.Concat("the encrypted file refused '", sql, "': ", Text(ErrorMessage(_db))));
        }

        List<IReadOnlyList<string?>> rows = [];
        try
        {
            var columns = ColumnCount(statement);
            while ((code = Step(statement)) == Row)
            {
                var row = new string?[columns];
                for (var column = 0; column < columns; column++)
                {
                    var text = ColumnText(statement, column);
                    row[column] = text == 0 ? null : Text(text);
                }

                rows.Add(row);
            }
        }
        finally
        {
            _ = Finalize(statement);
        }

        return code == Done
            ? rows
            : throw new InvalidOperationException(string.Concat("the encrypted file failed '", sql, "': ", Text(ErrorMessage(_db))));
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            _ = CloseV2(_db);
            _db = 0;
        }
    }

    private Refused? Run(string sql)
    {
        var code = Exec(_db, sql, 0, 0, 0);
        return code == Ok ? null : new Refused(code, Text(ErrorMessage(_db)));
    }

    private Refused? Key(byte[] material)
    {
        var code = KeyV2(_db, "main", material, material.Length);
        return code == Ok ? null : new Refused(code, Text(ErrorMessage(_db)));
    }

    private static string Immutable(string path) => string.Concat("file:",
        path.Replace("%", "%25", StringComparison.Ordinal).Replace("?", "%3f", StringComparison.Ordinal).Replace("#", "%23", StringComparison.Ordinal),
        "?immutable=1");

    private static string Text(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? throw new InvalidOperationException("sqlite returned no text");

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.Ordinal))
        {
            return 0;
        }

        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", architecture, "native", "libsqlite3mc.so"));
    }

    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenV2(string filename, out nint db, int flags, nint vfs);

    [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")]
    private static partial int CloseV2(nint db);

    [LibraryImport(Library, EntryPoint = "sqlite3_key_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int KeyV2(nint db, string name, byte[] key, int length);

    [LibraryImport(Library, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(nint db, string sql, nint callback, nint argument, nint error);

    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PrepareV2(nint db, string sql, int length, out nint statement, nint tail);

    [LibraryImport(Library, EntryPoint = "sqlite3_step")]
    private static partial int Step(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_count")]
    private static partial int ColumnCount(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
    private static partial nint ColumnText(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
    private static partial int Finalize(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_errmsg")]
    private static partial nint ErrorMessage(nint db);

    [LibraryImport(Library, EntryPoint = "sqlite3_errstr")]
    private static partial nint ErrorString(int code);
}
