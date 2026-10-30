using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.TerraBase.Rpc
{
    /// <summary>
    /// The REST pipeline that serves each RPC. The host captures its whole request pipeline with
    /// <see cref="RestRpcExtensions.UseTerraBaseRestPipeline"/>, so every call runs the same authentication,
    /// authorization, placement, admission and metering as the matching REST request.
    /// </summary>
    /// <param name="connection">
    /// Marks the in-process request's connection: <c>true</c> for a call that carries a user's bearer token, which the
    /// host serves as on its public client listener; <c>false</c> for an operator call, served as on its internal listener.
    /// </param>
    public sealed class RestPipeline(Action<IFeatureCollection, bool> connection)
    {
        /// <summary>The captured pipeline.</summary>
        public RequestDelegate Pipeline { get; private set; } = static _ => throw new InvalidOperationException("The REST pipeline is not captured.");

        internal Action<IFeatureCollection, bool> Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));

        /// <summary>Records the pipeline that follows the capture point.</summary>
        public void Capture(RequestDelegate pipeline)
        {
            Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        }
    }
}
