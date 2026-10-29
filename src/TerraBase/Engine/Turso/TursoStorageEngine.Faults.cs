using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    internal bool FileExists(TenantContext database) => File.Exists(Locate(database).Path);

    internal bool Evicted(TenantContext database)
    {
        var path = Locate(database).Path;
        return File.Exists(path) && Sidecars.Prepend(string.Empty).Select(sidecar => path + sidecar).Where(File.Exists).All(PageCache.Evicted);
    }

}
