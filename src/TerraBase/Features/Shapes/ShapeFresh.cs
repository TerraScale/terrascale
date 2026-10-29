using TerraBase.Features.Memory;

namespace TerraBase.Features.Shapes;
internal interface IShapeFreshStorage
{
    ValueTask<long> ReadShapeHeadAsync(CancellationToken cancellationToken);
    ValueTask<ShapeBody?> ReadShapeFreshAsync(ShapeSealDefinition shape, long offset, IMemoryBudget memory, CancellationToken cancellationToken);
}
