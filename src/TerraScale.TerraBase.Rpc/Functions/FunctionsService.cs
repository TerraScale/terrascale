using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraScale.TerraBase.Rpc.Functions
{
    /// <summary>Serves the functions contracts through TerraBase’s REST pipeline.</summary>
    public sealed class FunctionsService(RestApi api) : ServiceBase<IFunctionsService>, IFunctionsService
    {
        private static readonly FunctionsJson Json = FunctionsJson.Default;

        /// <inheritdoc />
        public async UnaryResult<IReadOnlyList<TerraBaseFunctionInfo>> ListAsync(string projectId, string database)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/functions");
            return RestApi.Read(answer, Json.IReadOnlyListTerraBaseFunctionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFunctionInfo> GetAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/functions/" + Uri.EscapeDataString(name));
            return RestApi.Read(answer, Json.TerraBaseFunctionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<IReadOnlyList<TerraBaseFunctionInvocation>> LogsAsync(string projectId, string database, string name, int? limit)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Database(projectId, database) + "/functions/" + Uri.EscapeDataString(name) + "/logs" + RestPaths.Query(("limit", RestPaths.Number(limit))));
            return RestApi.Read(answer, Json.IReadOnlyListTerraBaseFunctionInvocation);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DeleteAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database) + "/functions/" + Uri.EscapeDataString(name));
            RestApi.Ensure(answer);
            return true;
        }
    }
}
