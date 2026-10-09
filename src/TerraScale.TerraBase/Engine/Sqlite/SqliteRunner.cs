namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Runs SQLite calls on dedicated threads: Microsoft.Data.Sqlite has no true async I/O, so request threads hand calls
/// to a runner instead. A call cancelled before it starts never runs; once it runs, it completes.
/// </summary>
internal abstract class SqliteRunner
{
    /// <summary>Installed only by selected integration fixtures; absent on production runners.</summary>
    internal SqliteRunnerDiagnostics? Diagnostics
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public abstract Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken);

    public Task RunAsync(Action call, CancellationToken cancellationToken) => RunAsync(() =>
    {
        call();
        return true;
    }, cancellationToken);
}
