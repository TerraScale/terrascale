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

        /// <summary><c>GET …/functions/{name}/stats</c>; window is 1h, 24h (default) or 7d.</summary>
        UnaryResult<TerraBaseFunctionStats> StatsAsync(string projectId, string database, string name, string? window);

        /// <summary><c>DELETE …/functions/{name}</c>.</summary>
        UnaryResult<bool> DeleteAsync(string projectId, string database, string name);

        /// <summary><c>PUT …/functions/{name}</c>.</summary>
        UnaryResult<TerraBaseFunctionInfo> DeployAsync(string projectId, string database, string name, TerraBaseFunctionDeploy request);

        /// <summary><c>GET …/functions/{name}/secrets</c>; names and update times only.</summary>
        UnaryResult<IReadOnlyList<TerraBaseFunctionSecret>> ListSecretsAsync(string projectId, string database, string name);

        /// <summary><c>PUT …/functions/{name}/secrets/{secret}</c>.</summary>
        UnaryResult<TerraBaseFunctionSecret> SetSecretAsync(string projectId, string database, string name, string secret, TerraBaseFunctionSecretValue request);

        /// <summary><c>DELETE …/functions/{name}/secrets/{secret}</c>.</summary>
        UnaryResult<bool> DeleteSecretAsync(string projectId, string database, string name, string secret);

        /// <summary><c>POST …/functions/{name}/invoke</c>; query maps keys to values in event.query.</summary>
        UnaryResult<TerraBaseFunctionCall> InvokeAsync(string projectId, string database, string name, string body, IReadOnlyDictionary<string, string>? query = null);
    }
}
