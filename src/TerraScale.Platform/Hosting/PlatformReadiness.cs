namespace TerraScale.Platform.Hosting;

public sealed class PlatformReadiness
{
    private int _schemaReady;

    public bool SchemaReady => Volatile.Read(ref _schemaReady) == 1;

    public void MarkSchemaReady() => Volatile.Write(ref _schemaReady, 1);
}
