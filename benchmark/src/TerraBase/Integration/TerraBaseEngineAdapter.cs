namespace TerraBase.Integration;

/// <summary>
/// Composes the opened TerraBase engine with the producer and consumer views
/// used by integration modules.
/// </summary>
/// <remarks>
/// The adapter owns the supplied engine. Disposing this object stops the live
/// runtime and releases its configured storage provider exactly once.
/// </remarks>
public sealed class TerraBaseEngineAdapter : IAsyncDisposable
{
    private readonly TerraBaseEngine engine;
    private bool disposed;

    /// <summary>Creates integration views over an opened engine.</summary>
    /// <param name="engine">The runtime engine to expose to integration modules.</param>
    public TerraBaseEngineAdapter(TerraBaseEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Producer = new EngineDocumentProducer(engine);
        Consumer = new EngineDocumentConsumer(engine.Documents);
    }

    /// <summary>The engine owned by this adapter.</summary>
    public TerraBaseEngine Engine => engine;

    /// <summary>The write view over the engine's document store.</summary>
    public IDocumentProducer Producer { get; }

    /// <summary>The read view over the engine's document store.</summary>
    public IDocumentConsumer Consumer { get; }

    /// <summary>The engine metrics registry.</summary>
    public MetricsRegistry Metrics => engine.Metrics;

    /// <summary>The options used to open the engine.</summary>
    public TerraBaseOptions Options => engine.Options;

    /// <summary>Returns the current engine health report.</summary>
    public HealthReport Health() => engine.Health();

    /// <summary>Stops the runtime and releases the owned engine.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await engine.DisposeAsync().ConfigureAwait(false);
    }
}
