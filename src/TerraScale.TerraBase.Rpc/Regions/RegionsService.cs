using MagicOnion;
using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Contracts.Regions;

namespace TerraScale.TerraBase.Rpc.Regions
{
    /// <summary>Reads the public discovery endpoints through the anonymous client REST pipeline.</summary>
    public sealed class RegionsService(RestPipeline pipeline, IServiceScopeFactory scopes) : ServiceBase<IRegionsService>, IRegionsService
    {
        /// <inheritdoc />
        public async UnaryResult<TerraBaseRegions> ListAsync()
        {
            return RestApi.Read(await ReadAsync("/v1/regions"), RegionsJson.Default.TerraBaseRegions);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseHealth> HealthAsync()
        {
            var answer = await ReadAsync("/healthz");
            var health = RestApi.Read(answer, RegionsJson.Default.TerraBaseHealth);
            return answer.Headers.TryGetValue("X-TerraBase-Region", out var region) && region.Count == 1 && region[0] == health.Region
                ? health
                : throw RestApi.Fault(answer with { Status = StatusCodes.Status502BadGateway, Body = ReadOnlyMemory<byte>.Empty });
        }

        // Discovery is public: the request carries neither the caller's peer credential nor a token, so it enters the
        // client listener anonymously and can reach only what an anonymous public request can.
        private async Task<RestAnswer> ReadAsync(string path)
        {
            var request = RestApi.Request(Context, "GET", path, "application/json", forwardCredentials: false);
            using var response = new RestResponse(64 * 1024);
            await RestApi.RunAsync(pipeline, scopes, Context, request, response, response);
            return new(response.StatusCode, response.Headers, response.Written.ToArray());
        }
    }
}
