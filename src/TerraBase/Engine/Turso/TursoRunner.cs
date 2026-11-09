namespace TerraBase.Engine.Turso;

internal abstract class TursoRunner
{
    internal TursoRunnerDiagnostics? Diagnostics
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
