using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Functions
{
    /// <summary>The functions API, authorized and metered through the corresponding REST routes.</summary>
    public interface IFunctionsService : IService<IFunctionsService>
    {
        /// <summary><c>GET …/functions</c>.</summary>
        UnaryResult<IReadOnlyList<TerraBaseFunctionInfo>> ListAsync(string projectId, string database);

        /// <summary><c>GET …/functions/{name}</c>.</summary>
        UnaryResult<TerraBaseFunctionInfo> GetAsync(string projectId, string database, string name);

        /// <summary><c>GET …/functions/{name}/logs</c>.</summary>
        UnaryResult<IReadOnlyList<TerraBaseFunctionInvocation>> LogsAsync(string projectId, string database, string name, int? limit);

        /// <summary><c>DELETE …/functions/{name}</c>.</summary>
        UnaryResult<bool> DeleteAsync(string projectId, string database, string name);

#pragma warning disable CS1591
        UnaryResult<TerraBaseFunctionInfo> DeployAsync(string projectId, string database, string name, TerraBaseFunctionDeploy request);

        UnaryResult<TerraBaseFunctionCall> InvokeAsync(string projectId, string database, string name, string body);
#pragma warning restore CS1591
    }
}
