using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.TerraBase.Rpc.Changes
{
    // The stream form of RestApi: preserves the same REST pipeline and forwards one bounded line at a time.
    // Awaiting each downstream write propagates backpressure through the REST writer; no unbounded channel or buffer.
    internal sealed class RestLines(RestPipeline pipeline, IServiceScopeFactory scopes)
    {
        internal async Task SendAsync(ServiceContext call, string pathAndQuery, string mediaType,
            Func<ReadOnlyMemory<byte>, string?, Task> onLine, Action<IHeaderDictionary>? headers = null)
        {
            ArgumentNullException.ThrowIfNull(call);
            var request = RestApi.Request(call, "GET", pathAndQuery, mediaType, forwardCredentials: true);
            headers?.Invoke(request.Headers);
            await using var response = new LineResponse(mediaType, onLine);
            await RestApi.RunAsync(pipeline, scopes, call, request, response, response).ConfigureAwait(false);
            RestApi.Ensure(new RestAnswer(response.StatusCode, response.Headers, response.ErrorBody));
        }
    }
}
