using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// Change stamps for read caches. A stamp changes when a write to what it covers starts and again once
/// the write commits, and when a database is deleted. A value read from storage after taking a stamp may
/// be cached under that stamp and served while the stamp is unchanged: it is then never older than an
/// acknowledged write.
/// </summary>
public interface IWriteStamps
{
    /// <summary>Stamp of one document, scoped to organization, project and database; null addresses only system storage.</summary>
    long Document(TenantContext? database, string collection, string id);

    /// <summary>Stamp of a tenant collection, or a system collection when database is null: changes on every document write.</summary>
    long Collection(TenantContext? database, string collection);
}
