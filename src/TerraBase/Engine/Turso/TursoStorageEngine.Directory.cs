using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private static FileStream Own(string dataDirectory)
    {
        FileStream ownership;
        try
        {
            Directory.CreateDirectory(dataDirectory);

            ownership = new FileStream(Path.Combine(dataDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsLockConflict(error))
        {
            throw new TerraBaseConfigurationException("data-directory is in use by another TerraBase process; stop it, then start again");
        }
        catch (IOException)
        {

            throw new TerraBaseConfigurationException("data-directory must be a directory where this process can create and lock files");
        }
        catch (UnauthorizedAccessException)
        {
            throw new TerraBaseConfigurationException("data-directory must be a directory this process can write to");
        }

        try
        {
            CheckLayout(dataDirectory, ownership);
            return ownership;
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private static void CheckLayout(string dataDirectory, FileStream ownership)
    {
        var recorded = ownership.Length > 0;
        if (recorded)
        {
            _ = DirectoryVersion(dataDirectory, ownership);
        }

        var entries = Directory.EnumerateFileSystemEntries(dataDirectory)
            .Select(entry => Path.GetFileName(entry))
            .Where(name => !string.Equals(name, LockFileName, StringComparison.Ordinal));
        if (recorded ? entries.Any(IsFlatDatabaseFile) : entries.Any())
        {
            throw new TerraBaseConfigurationException(PreTenancyLayout);
        }

        if (!recorded)
        {
            ownership.Write(LayoutRecord);
            ownership.Flush(flushToDisk: true);
        }
    }

    private const string MigrationFile = ".terrabase.migration";

    private static int DirectoryVersion(string directory, FileStream ownership)
    {
        var journal = Path.Combine(directory, MigrationFile);
        RejectLink(journal);
        var text = File.Exists(journal) ? File.ReadAllText(journal) : RecordedFormat(ownership);
        var from = text;
        if (text.StartsWith("migrating:", StringComparison.Ordinal))
        {
            var parts = text["migrating:".Length..].Split("->", StringSplitOptions.None);
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var target))
            {
                throw new TerraBaseConfigurationException("the storage migration record is malformed");
            }
            StorageMigrations.RequirePath(target);
            from = parts[0];
        }
        else if (text == StorageMigrations.PreviousId)
        {
            from = "23";
        }

        if (!int.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new TerraBaseConfigurationException("the data directory has an unrecognized storage format; no migration path exists");
        }
        StorageMigrations.RequirePath(version);
        return version;
    }

    private static byte[] EncodingBytes(string value) => Encoding.ASCII.GetBytes(value);

    private static void WriteFormat(FileStream ownership, string value)
    {
        var bytes = EncodingBytes(value);
        ownership.Position = 0;
        ownership.Write(bytes);
        ownership.SetLength(bytes.Length);
        ownership.Flush(flushToDisk: true);
    }

    private static string RecordedFormat(FileStream ownership)
    {
        if (ownership.Length > 64)
        {
            return "unversioned";
        }

        Span<byte> record = stackalloc byte[(int)ownership.Length];
        ownership.Position = 0;
        ownership.ReadExactly(record);
        return Encoding.ASCII.GetString(record);
    }

    internal static bool IsLockConflict(IOException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.GetType() == typeof(IOException) && error.HResult is 11 or unchecked((int)0x80070020) or unchecked((int)0x80070021);
    }

    private static bool IsReservedRootName(ref FileSystemEntry entry)
    {
        foreach (var reserved in ReservedRootNames)
        {
            if (entry.FileName.SequenceEqual(reserved))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFlatDatabaseFile(string name) =>
        !ReservedRootNames.Contains(name)
        && (name.EndsWith(FileExtension, StringComparison.Ordinal)
            || Sidecars.Any(sidecar => name.EndsWith(FileExtension + sidecar, StringComparison.Ordinal)));
}
