using MagicOnion.Serialization.MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.TerraBase.Rpc
{
    /// <summary>Hosts the TerraBase MagicOnion services over a REST pipeline.</summary>
    public static class RestRpcExtensions
    {
        /// <summary>Registers MagicOnion with the contract serializer and message limits, and the REST pipeline.</summary>
        public static IServiceCollection AddTerraBaseRestRpc(this IServiceCollection services, Action<IFeatureCollection, bool> connection)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddSingleton(new RestPipeline(connection));
            services.AddSingleton<RestApi>();
            services.AddGrpc(grpc =>
            {
                grpc.MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes;
                grpc.MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes;
            });
            services.AddMagicOnion(magicOnion => magicOnion.MessageSerializer = MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
            return services;
        }

        /// <summary>Captures the request pipeline from this point; call it first.</summary>
        public static IApplicationBuilder UseTerraBaseRestPipeline(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            var pipeline = app.ApplicationServices.GetRequiredService<RestPipeline>();
            return app.Use(next =>
            {
                pipeline.Capture(next);
                return next;
            });
        }

        /// <summary>Maps every TerraBase MagicOnion service.</summary>
        public static IEndpointRouteBuilder MapTerraBaseRestRpc(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            endpoints.MapMagicOnionService([typeof(RestApi).Assembly]);
            return endpoints;
        }
    }
}
