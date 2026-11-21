namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    internal TursoSessionPool.ExternalOpen OpenBackupReader() => pool.OpenExternal(this);

    internal void CloseBackupReader() => pool.CloseExternal(this);
}
