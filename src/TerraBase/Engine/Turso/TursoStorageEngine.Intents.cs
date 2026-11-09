using TerraBase.Configuration;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    internal const string IntentName = "_intents";

    internal const string IntentFile = IntentName + FileExtension;
    private TursoDatabaseStore? _intents;

    internal TursoDatabaseStore? IntentStore => Volatile.Read(ref _intents);

    private async Task OpenIntentStoreLockedAsync(CancellationToken cancellationToken)
    {
        if (_intents is not null)
        {
            return;
        }

        var system = LocateSystem();
        if (!File.Exists(system) && (Directory.Exists(system)
            || Sidecars.Any(suffix => File.Exists(system + suffix) || Directory.Exists(system + suffix))))
        {
            throw new IOException("a database create target already contains files");
        }
        var path = Path.Combine(_dataDirectory, IntentName + FileExtension);
        RejectDatabaseLinks(path);
        CheckIntentJournal();
        var created = await CreateFileAsync(SystemName, path, cancellationToken).ConfigureAwait(false);
        var openingToken = created ? CancellationToken.None : cancellationToken;
        var store = CreateSystemStore(path);
        try
        {
            await store.BootstrapAsync(openingToken).ConfigureAwait(false);
        }
        catch
        {
            await Io.RunAsync(store.Dispose, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        Volatile.Write(ref _intents, store);
    }

    private void CheckIntentJournal()
    {
        var path = Path.Combine(_dataDirectory, IntentName + FileExtension);
        RejectDatabaseLinks(path);
        if (!File.Exists(path) && SystemDatabaseExists)
        {
            throw new TerraBaseConfigurationException("the data directory is missing its storage operation intent journal");
        }
    }

    private async Task CloseIntentStoreAsync()
    {
        if (Interlocked.Exchange(ref _intents, null) is { } store)
        {
            await Io.RunAsync(store.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
