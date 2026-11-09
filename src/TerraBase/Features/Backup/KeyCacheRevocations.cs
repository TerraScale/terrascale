namespace TerraBase.Features.Backup;

internal sealed class KeyCacheRevocations
{
    internal event Action<string>? Shredded;

    internal void Forget(string scope) => Shredded?.Invoke(scope);
}
