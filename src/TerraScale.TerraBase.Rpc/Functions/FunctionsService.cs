using System.Text;
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
        public async UnaryResult<TerraBaseFunctionStats> StatsAsync(string projectId, string database, string name, string? window)
        {
            var answer = await api.SendAsync(Context, "GET", Item(projectId, database, name) + "/stats" + RestPaths.Query(("window", window)));
            return RestApi.Read(answer, Json.TerraBaseFunctionStats);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DeleteAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Database(projectId, database) + "/functions/" + Uri.EscapeDataString(name));
            RestApi.Ensure(answer);
            return true;
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFunctionInfo> DeployAsync(string projectId, string database, string name, TerraBaseFunctionDeploy request)
        {
            var answer = await api.SendAsync(Context, "PUT", Item(projectId, database, name), RestApi.Json(request, Json.TerraBaseFunctionDeploy));
            return RestApi.Read(answer, Json.TerraBaseFunctionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<IReadOnlyList<TerraBaseFunctionSecret>> ListSecretsAsync(string projectId, string database, string name)
        {
            var answer = await api.SendAsync(Context, "GET", Item(projectId, database, name) + "/secrets");
            return RestApi.Read(answer, Json.IReadOnlyListTerraBaseFunctionSecret);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFunctionSecret> SetSecretAsync(string projectId, string database, string name, string secret, TerraBaseFunctionSecretValue request)
        {
            var answer = await api.SendAsync(Context, "PUT", Item(projectId, database, name) + "/secrets/" + Uri.EscapeDataString(secret), RestApi.Json(request, Json.TerraBaseFunctionSecretValue));
            return RestApi.Read(answer, Json.TerraBaseFunctionSecret);
        }

        /// <inheritdoc />
        public async UnaryResult<bool> DeleteSecretAsync(string projectId, string database, string name, string secret)
        {
            var answer = await api.SendAsync(Context, "DELETE", Item(projectId, database, name) + "/secrets/" + Uri.EscapeDataString(secret));
            RestApi.Ensure(answer);
            return true;
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseFunctionCall> InvokeAsync(string projectId, string database, string name, string body)
        {
            var answer = await api.SendAsync(Context, "POST", Item(projectId, database, name) + "/invoke", Encoding.UTF8.GetBytes(body), "application/json");
            if (!IsFunctionAnswer(answer))
            {
                RestApi.Ensure(answer);
            }

            var contentType = answer.Headers.TryGetValue("Content-Type", out var values) ? values.ToString() : null;
            return new TerraBaseFunctionCall(answer.Status, contentType, Encoding.UTF8.GetString(answer.Body.Span));
        }

        private static string Item(string projectId, string database, string name)
        {
            return RestPaths.Database(projectId, database) + "/functions/" + Uri.EscapeDataString(name);
        }

        private static bool IsFunctionAnswer(RestAnswer answer) =>
            answer.Headers.TryGetValue("X-TerraScale-Function-Response", out var marker) && marker.ToString() == "1";
    }
}
