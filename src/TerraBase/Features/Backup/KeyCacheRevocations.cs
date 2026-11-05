namespace TerraBase.Features.Backup;

// Shared by the platform and customer key managers of one custody store. Disposing a manager
// unregisters it; the bounded customer request/shipper pools also bound this subscription set.
internal sealed class KeyCacheRevocations
{
    internal event Action<string>? Shredded;

    internal void Forget(string scope) => Shredded?.Invoke(scope);
}
