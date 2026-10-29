namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public ValueTask<long> ReadShapeFrontierAsync(CancellationToken cancellationToken) =>
        RunAsync(ShapeFeedHead, cancellationToken);

    private long ShapeFeedHead()
    {
        CountRead("shape");
        return Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar() is long seq ? seq : 0;
    }
}
