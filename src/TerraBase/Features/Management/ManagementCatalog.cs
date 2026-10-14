namespace TerraBase.Features.Management;

/// <summary>The immutable supported-version row carried with dependent system replication groups.</summary>
public static class ManagementCatalog
{
    /// <summary>The collection holding the management catalog version.</summary>
    public const string Collection = "catalog";
    /// <summary>The immutable version row's document key.</summary>
    public const string VersionId = "version";
    /// <summary>The only management catalog version this build accepts.</summary>
    public const string Version = "1";
}
