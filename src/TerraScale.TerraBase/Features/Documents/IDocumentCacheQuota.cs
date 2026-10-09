using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>Reconciles retained document cache bytes before request memory admission.</summary>
public interface IDocumentCacheQuota
{
    /// <summary>Releases the organization's cache excess after a plan downgrade.</summary>
    void Reconcile(TenantContext tenant);
}
