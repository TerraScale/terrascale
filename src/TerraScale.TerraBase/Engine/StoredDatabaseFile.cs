using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>A file or symbolic link in the data directory, with its database identity when its path is recognized.</summary>
/// <param name="Path">The full file path.</param>
/// <param name="Database">The database named by the path, or null for unknown files. The plan is not encoded on disk.</param>
public sealed record StoredDatabaseFile(string Path, TenantContext? Database);
