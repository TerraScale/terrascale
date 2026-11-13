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
            _ = ReadDirectoryFormat(ownership);
        }

        var entries = Directory.EnumerateFileSystemEntries(dataDirectory)
            .Select(entry => Path.GetFileName(entry))
            .Where(name => !string.Equals(name, LockFileName, StringComparison.Ordinal)
                && name is not (".master-key-history" or ".master-key-history.writing"));
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

    private const string MigratingPrefix = "migrating:";

    private readonly record struct DirectoryFormat(int Format, int? MigratingFrom)
    {
        public bool NeedsMigration => MigratingFrom is null ? Format < StorageFormat.Target : Format <= StorageFormat.Target;

        public string Journal => $"{MigratingPrefix}{StorageFormat.Name(MigratingFrom ?? Format)}->{StorageFormat.Name(StorageFormat.Target)}";
    }

    private static DirectoryFormat ReadDirectoryFormat(FileStream ownership)
    {
        var text = RecordedFormat(ownership);
        var format = text.StartsWith(MigratingPrefix, StringComparison.Ordinal)
            ? text[MigratingPrefix.Length..].Split("->", StringSplitOptions.None) switch
            {
                [var from, var to] when StorageFormat.Parse(from) is { } start && StorageFormat.Parse(to) is { } end && start < end
                    => new DirectoryFormat(end, start),
                _ => (DirectoryFormat?)null,
            }
            : StorageFormat.Parse(text) is { } recorded ? new DirectoryFormat(recorded, null) : null;
        if (format is not { } known)
        {
            throw new TerraBaseConfigurationException(
                $"the data directory's storage format record is not recognised; this build supports storage formats {StorageFormat.SupportedFrom} to {StorageFormat.SupportedTo}");
        }

        foreach (var held in (int[])[known.MigratingFrom ?? known.Format, known.Format])
        {
            if (!StorageFormat.IsSupported(held))
            {
                throw new TerraBaseConfigurationException(StorageFormat.Refusal("the data directory", held));
            }
        }

        return known;
    }

    private static void WriteFormat(FileStream ownership, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        ownership.Position = 0;
        ownership.Write(bytes);
        ownership.SetLength(bytes.Length);
        ownership.Flush(flushToDisk: true);
    }

    private static string RecordedFormat(FileStream ownership)
    {
        if (ownership.Length > 64)
        {
            return string.Empty;
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
