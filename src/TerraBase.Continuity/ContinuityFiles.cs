using System.Globalization;
using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed class StorageLayout(StorageEngineOptions storage)
{
    private const string StagingDirectoryName = ".continuity";
    private const string SealedSegmentExtension = ".tbseg";
    private const string SnapshotDirectoryName = "snapshots";

    public string DatabaseDirectory(Ulid databaseId) => Path.Combine(storage.RootDirectory, databaseId.ToString());

    public string DatabaseFile(Ulid databaseId) => Path.Combine(DatabaseDirectory(databaseId), string.Concat(databaseId.ToString(), ".db"));

    public bool Exists(Ulid databaseId) => File.Exists(DatabaseFile(databaseId));

    public bool DirectoryExists(Ulid databaseId) => Directory.Exists(DatabaseDirectory(databaseId));

    public string StagingRoot => Path.Combine(storage.RootDirectory, StagingDirectoryName);

    public string StagingDirectory(Ulid operationId) => Path.Combine(StagingRoot, operationId.ToString());

    public static string DatabaseDirectoryIn(string root, Ulid databaseId) => Path.Combine(root, databaseId.ToString());

    public static string DatabaseFileIn(string root, Ulid databaseId) =>
        Path.Combine(DatabaseDirectoryIn(root, databaseId), string.Concat(databaseId.ToString(), ".db"));

    public static string SnapshotDirectoryIn(string root, Ulid databaseId) => Path.Combine(DatabaseDirectoryIn(root, databaseId), SnapshotDirectoryName);

    public static string SnapshotFileIn(string root, Ulid databaseId, Ulid snapshotId) =>
        Path.Combine(SnapshotDirectoryIn(root, databaseId), string.Concat(snapshotId.ToString(), ".db"));

    public static string SegmentFileIn(string root, Ulid databaseId, string origin, long firstSequence, long lastSequence) =>
        Path.Combine(
            DatabaseDirectoryIn(root, databaseId),
            string.Concat(
                databaseId.ToString(),
                ".",
                origin,
                ".",
                firstSequence.ToString(CultureInfo.InvariantCulture),
                "-",
                lastSequence.ToString(CultureInfo.InvariantCulture),
                SealedSegmentExtension));
}

internal sealed record ContinuityFilesChanged;

internal sealed record ContinuityIoFailure(string Message, string Code);

internal readonly union ContinuityFileOutcome(ContinuityFilesChanged, ContinuityIoFailure);

internal readonly union ContinuityFileListOutcome(IReadOnlyList<string>, ContinuityIoFailure);

internal readonly union ContinuityOpenFileOutcome(Stream, ContinuityIoFailure);

internal static class ContinuityFiles
{
    public static ContinuityFileOutcome CreateDirectory(string path) => Run(() => Directory.CreateDirectory(path));

    public static ContinuityFileOutcome DeleteDirectory(string path) => Run(() =>
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    });

    public static ContinuityFileOutcome MoveDirectory(string source, string destination) => Run(() => Directory.Move(source, destination));

    public static ContinuityFileOutcome MoveFile(string source, string destination) => Run(() => File.Move(source, destination, overwrite: false));

    public static ContinuityFileOutcome CopyFile(string source, string destination) => Run(() => File.Copy(source, destination, overwrite: false));

    public static ContinuityFileOutcome DeleteFile(string path) => Run(() => File.Delete(path));

    public static ContinuityOpenFileOutcome OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException exception)
        {
            return Failure(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }
    }

    public static ContinuityFileListOutcome ListFiles(string directory, string pattern) =>
        List(directory, () => [.. Directory.EnumerateFiles(directory, pattern)]);

    public static ContinuityFileListOutcome ListDirectories(string directory) =>
        List(directory, () => [.. Directory.EnumerateDirectories(directory)]);

    private static ContinuityFileListOutcome List(string directory, Func<IReadOnlyList<string>> enumerate)
    {
        if (!Directory.Exists(directory))
        {
            return Listed([]);
        }

        try
        {
            return Listed(enumerate());
        }
        catch (IOException exception)
        {
            return Failure(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }

        static ContinuityFileListOutcome Listed(IReadOnlyList<string> files) => files;
    }

    private static ContinuityFileOutcome Run(Action action)
    {
        try
        {
            action();
            return new ContinuityFilesChanged();
        }
        catch (IOException exception)
        {
            return Failure(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }
    }

    private static ContinuityIoFailure Failure(string message) => new(string.Concat("local file operation failed: ", message), "continuity_io");
}
