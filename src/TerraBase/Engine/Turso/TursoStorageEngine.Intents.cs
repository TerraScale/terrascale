using TerraBase.Configuration;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    internal const string IntentName = "_intents";

    /// <summary>The intent journal's file name, which tells its store apart: it opens with SYSTEM's name and semantics.</summary>
    internal const string IntentFile = IntentName + FileExtension;
    private TursoDatabaseStore? _intents;

    internal TursoDatabaseStore? IntentStore => Volatile.Read(ref _intents);

    // The caller holds creation and reuses its admission. Journal writes never need the application catalog's writer.
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
        // System semantics suppress recursive intent recording and fail the node closed on native journal damage.
        var store = new TursoDatabaseStore(SystemName, path, Pool, Io, _pragmas, null, Time,
            RecordCommittedWrite, _logger, _schemaMemory, _schemaEvaluations, _storageReads, Encryption)
        {
            QueryBudget = QueryBudget,
            Member = Member,
            Checkpoint = _checkpoint,
            Ledger = Ledger,
            WriterHeld = _writerHeld,
            Transactions = _transactions,
        };
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

    // A journal is published before SYSTEM: once SYSTEM exists its absence cannot mean a fresh node.
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
