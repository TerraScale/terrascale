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
            var format = RecordedFormat(ownership);
            if (format != StorageFormat.Id)
            {
                throw new TerraBaseConfigurationException(
                    $"data directory has storage format {format}; this build writes {StorageFormat.Id}; start with an empty data directory (no automatic migration)");
            }
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

    private static string RecordedFormat(FileStream ownership)
    {
        if (ownership.Length > 32)
        {
            return "unversioned";
        }

        Span<byte> record = stackalloc byte[(int)ownership.Length];
        ownership.Position = 0;
        ownership.ReadExactly(record);
        var text = Encoding.ASCII.GetString(record);
        return text.Contains('-', StringComparison.Ordinal) && !text.AsSpan().ContainsAnyExcept(FormatCharacters) ? text : "unversioned";
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
