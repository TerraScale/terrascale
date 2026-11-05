namespace TerraBase.Engine.Turso;

public static class StorageFormat
{
    public const int Version = 23;

    public static string Id { get; } = $"{Version}-{Convert.ToHexStringLower(TursoDatabaseStore.FormatDigest)[..12]}";
}
