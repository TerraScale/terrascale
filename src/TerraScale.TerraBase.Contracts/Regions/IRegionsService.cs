using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Regions
{
    /// <summary>The anonymous region catalog and public health APIs.</summary>
    public interface IRegionsService : IService<IRegionsService>
    {
        /// <summary>GET /v1/regions.</summary>
        UnaryResult<TerraBaseRegions> ListAsync();
        /// <summary>GET /healthz, with the answering region validated against its response header.</summary>
        UnaryResult<TerraBaseHealth> HealthAsync();
    }
}
