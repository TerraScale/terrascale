namespace TerraBase.Features.Documents;

/// <summary><see cref="IShapeService"/> under <c>core=anna</c>: shapes need the disk tier's log, so they answer 503 until it ships.</summary>
internal sealed class AnnaShapeService : IShapeService
{
    public ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ShapeReadResult>(AnnaDocumentService.NeedsDiskTier);
}
