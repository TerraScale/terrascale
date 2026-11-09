using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.DotNet.TerraBase;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Platform.Persistence;

public static class DashboardStorage
{
    public const string ProjectId = "00000000000000000000000005";
    public const string Database = "ts-dashboard";
    public const string WriteHeader = "TerraScale-Write";

    public static IServiceCollection AddDashboardStorage(this IServiceCollection services)
    {
        services.AddSingleton<Transport>();
        services.AddSingleton(provider => new TerraBaseSqlClient(provider.GetRequiredService<Transport>().Client,
            new TerraBaseSqlClientOptions { BaseAddress = TerraBaseRpcClient.Origin(provider.GetRequiredService<IHostEnvironment>()) }).Database(ProjectId, Database));
        services.AddHostedService<StorageSweeper>();
        services.AddPooledDbContextFactory<DashboardDbContext>((provider, options) => options.UseTerraBase(provider.GetRequiredService<TerraBaseSqlDatabase>()));
        return services;
    }

    public sealed class Transport(IDashboardIdentity identity) : IDisposable
    {
        public HttpClient Client { get; } = new(new Home(identity) { InnerHandler = TerraBaseHttp.CreateHandler() });

        public void Dispose()
        {
            Client.Dispose();
        }
    }

    private sealed class Home(IDashboardIdentity identity) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            request.Headers.Remove(TerraBaseRpc.PeerCredentialHeader);
            request.Headers.Add(TerraBaseRpc.PeerCredentialHeader, credentials.TerraBasePeerSecret);
            request.Headers.Remove(WriteHeader);
            request.Headers.Add(WriteHeader, "home");
            return await ServerTiming.RpcAsync("state/sql/" + request.RequestUri?.Segments[^1], true,
                () => base.SendAsync(request, cancellationToken));
        }
    }
}
