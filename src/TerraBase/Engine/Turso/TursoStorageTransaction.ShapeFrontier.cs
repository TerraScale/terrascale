namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public ValueTask<long> ReadShapeFrontierAsync(CancellationToken cancellationToken) =>
        RunAsync(ShapeHead, cancellationToken);
}
