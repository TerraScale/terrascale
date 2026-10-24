namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    internal void OpenBackupReader() => pool.OpenExternal(this);

    internal void CloseBackupReader() => pool.CloseExternal(this);
}
