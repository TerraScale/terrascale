namespace TerraBase.Engine;

internal interface IWrappedKeyFiles
{
    Task<IReadOnlyDictionary<string, long>> RewrapAsync(CancellationToken cancellationToken);
}
