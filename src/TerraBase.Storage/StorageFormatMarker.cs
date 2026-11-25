using System.Text;

namespace TerraBase.Storage;

public sealed record StorageFormatAccepted;

public sealed record StoredFormatMismatch(string Expected, string Recorded);

public sealed record StorageFormatMarkerUnavailable(string Message);

public readonly union StorageFormatMarkerOutcome(StorageFormatAccepted, StoredFormatMismatch, StorageFormatMarkerUnavailable);

public static class StorageFormatMarker
{
    private const string Name = ".storage-format";

    public static StorageFormatMarkerOutcome CheckOrCreate(string directory, string formatId)
    {
        var path = Path.Combine(directory, Name);
        if (File.Exists(path))
        {
            return Check(path, formatId);
        }

        if (FileIo.CreateDirectory(directory) is { } directoryError)
        {
            return new StorageFormatMarkerUnavailable(directoryError);
        }

        var temp = Path.Combine(directory, string.Concat(Name, ".", Path.GetRandomFileName()));
        if (FileIo.OpenHandle(temp, FileMode.CreateNew) is not { } handle)
        {
            return new StorageFormatMarkerUnavailable("could not create a temporary storage format marker");
        }

        try
        {
            using (handle)
            {
                if (FileIo.Write(handle, Encoding.UTF8.GetBytes(formatId), 0, fsync: true) is { } writeError)
                {
                    return new StorageFormatMarkerUnavailable(writeError);
                }
            }

            var moveError = FileIo.Move(temp, path);
            return moveError is null ? new StorageFormatAccepted()
                : File.Exists(path) ? Check(path, formatId)
                : new StorageFormatMarkerUnavailable(moveError);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static StorageFormatMarkerOutcome Check(string path, string formatId)
    {
        if (FileIo.ReadAll(path) is not { } bytes)
        {
            return new StorageFormatMarkerUnavailable("could not read the storage format marker");
        }

        var recorded = Encoding.UTF8.GetString(bytes);
        return string.Equals(recorded, formatId, StringComparison.Ordinal)
            ? new StorageFormatAccepted()
            : new StoredFormatMismatch(formatId, recorded);
    }
}
