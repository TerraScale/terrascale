using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class RequestReadBudgetContractTests
{
    [Test]
    public async Task FormActionContextRemainsCurrentOnlyUntilItSendsAWrite()
    {
        using var wire = new Wire();
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "POST" } }, async () =>
        {
            var loaded = ServerTiming.Now();
            await Assert.That(loaded?.Current).IsTrue();
            using var client = wire.CreateClient("mutation");
            using var response = await client.PostAsync("https://auth.terrascale.tech/v1/membership", null);
            await Assert.That(loaded?.Current).IsFalse();
            await Assert.That(ServerTiming.Now()?.Current).IsTrue();
        }, NullLogger.Instance);
    }

    [Test]
    public async Task AuthDirectoryReadsCoalesceWithinOneGetAndRefreshAfterWritesOrAnotherPage()
    {
        using var wire = new Wire();
        wire.Release.TrySetResult();
        var gateway = new TsAuthGateway(new Identity(), wire, TimeProvider.System);
        var session = Scope().Session!;
        async Task Read() => await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => gateway.ListOrganizationsAsync(session, CancellationToken.None)));
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            await Read();
            await Assert.That(wire.Listings).IsEqualTo(1);
            await gateway.ListOrganizationsAsync(session with { AccessToken = "different" }, CancellationToken.None);
            await Assert.That(wire.Listings).IsEqualTo(2);
            using var client = wire.CreateClient("mutation");
            using var response = await client.PostAsync("https://auth.terrascale.tech/v1/membership", null);
            await Read();
            await Assert.That(wire.Listings).IsEqualTo(3);
        }, NullLogger.Instance);
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, Read, NullLogger.Instance);
        await Assert.That(wire.Listings).IsEqualTo(4);
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "POST" } }, Read, NullLogger.Instance);
        await Assert.That(wire.Listings).IsEqualTo(7);
    }

    private sealed class Identity : IDashboardIdentity
    {
        public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DashboardCredentials("synthetic", "", new("", ""), ""));
    }

    [Test]
    public async Task ParallelPanelsShareCatalogAndNameResolutionOnlyInsideOneGet()
    {
        using var wire = new Wire();
        var backend = new DatabaseBackend(wire, new Tokens());
        var scope = Scope();
        var home = DatabaseRegion.FromId("gru")!;
        for (var request = 1; request <= 2; request++)
            await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
            {
                var regions = Enumerable.Range(0, 5).Select(_ => backend.RegionsAsync(CancellationToken.None)).ToArray();
                await Assert.That(wire.Catalogs).IsEqualTo(request);
                wire.Release.TrySetResult();
                await Task.WhenAll(regions);
                await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => backend.ListAsync(scope, home, CancellationToken.None)));
                await Assert.That(wire.Listings).IsEqualTo(request);
            }, NullLogger.Instance);
    }

    [Test]
    public async Task ScopeChangesAndMutationsNeverReuseOldDatabaseAuthority()
    {
        using var wire = new Wire();
        wire.Release.TrySetResult();
        var backend = new DatabaseBackend(wire, new Tokens());
        var scope = Scope();
        var home = DatabaseRegion.FromId("gru")!;
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            await backend.ListAsync(scope, home, CancellationToken.None);
            await backend.ListAsync(scope with { AccessToken = "different" }, home, CancellationToken.None);
            using var mutation = wire.CreateClient("mutation");
            using var response = await mutation.PostAsync("https://auth.terrascale.tech/v1/membership", null);
            await backend.ListAsync(scope, home, CancellationToken.None);
            await Assert.That(wire.Listings).IsEqualTo(3);
        }, NullLogger.Instance);
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "POST" } }, async () =>
        {
            await backend.ListAsync(scope, home, CancellationToken.None);
            await backend.ListAsync(scope, home, CancellationToken.None);
            await Assert.That(wire.Listings).IsEqualTo(5);
        }, NullLogger.Instance);
    }

    private static DatabaseScope Scope() => new("user", "access", "01JA0000000000000000000002", "Org", "01JA0000000000000000000001", "Project")
    { Session = new("user", "Synthetic", "synthetic@example.test", "access", null, DateTimeOffset.UtcNow.AddMinutes(5)) };

    private sealed class Tokens : IProjectAccessTokens
    {
        public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken) => Task.FromResult("scoped");
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Catalogs { get; private set; }
        public int Listings { get; private set; }
        public HttpClient CreateClient(string name) => new(new ServerTiming.Handler { InnerHandler = this }, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var catalog = request.RequestUri!.AbsolutePath == "/v1/regions";
            if (catalog) { Catalogs++; await Release.Task.WaitAsync(cancellationToken); }
            else if (request.Method == HttpMethod.Get) Listings++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(catalog ? "{\"regions\":[\"gru\",\"fra\"]}" : "{\"items\":[],\"next\":null}", Encoding.UTF8, "application/json")
            };
        }
    }
}
