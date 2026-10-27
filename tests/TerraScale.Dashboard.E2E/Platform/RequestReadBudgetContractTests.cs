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
    [Arguments("organizations")]
    [Arguments("projects")]
    public async Task PublishingStorageAllowancesDoesNotDiscardFreshAccountAuthority(string resource)
    {
        using var wire = new Wire();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Mutating = () => finished.Task;
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var loaded = ServerTiming.Now();
            var reads = 0;
            Task<int> Read() => ServerTiming.ReadAsync("membership", () => Task.FromResult(++reads));
            await Read();
            using var client = wire.CreateClient("plan");
            using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:8081/admin/{resource}/01JA0000000000000000000001/plan");
            request.Options.Set(TerraBasePlanSync.Publication, new("hobby"));
            var publishing = client.SendAsync(request);
            await Assert.That(loaded?.Current).IsTrue();
            await Read();
            finished.SetResult();
            using var response = await publishing;
            await Assert.That(loaded?.Current).IsTrue();
            await Read();
            await Assert.That(reads).IsEqualTo(1);
            using var mutation = await client.PostAsync("https://auth.terrascale.tech/v1/membership", null);
            await Assert.That(loaded?.Current).IsFalse();
            await Read();
            await Assert.That(reads).IsEqualTo(2);
        }, NullLogger.Instance);
    }

    [Test]
    [Arguments("PUT", "https://auth.terrascale.tech/admin/organizations/01JA0000000000000000000001/plan")]
    [Arguments("PUT", "https://api.terrascale.tech/admin/organizations/01JA0000000000000000000001/plan")]
    [Arguments("POST", "http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001/plan")]
    [Arguments("PUT", "http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001/plan/extra")]
    [Arguments("PUT", "http://127.0.0.1:8081/admin/organizations/invalid/plan")]
    [Arguments("PUT", "http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001/plan/")]
    [Arguments("PUT", "http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001")]
    public async Task PlanPathLookalikesStillInvalidateAccountAuthority(string method, string url)
    {
        using var wire = new Wire();
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var loaded = ServerTiming.Now();
            using var client = wire.CreateClient("mutation");
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Options.Set(TerraBasePlanSync.Publication, new("hobby"));
            using var response = await client.SendAsync(request);
            await Assert.That(loaded?.Current).IsFalse();
        }, NullLogger.Instance);
    }

    [Test]
    public async Task AnUnmarkedPrivatePlanWriteStillInvalidatesAccountAuthority()
    {
        using var wire = new Wire();
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var loaded = ServerTiming.Now();
            using var client = wire.CreateClient("mutation");
            using var response = await client.PutAsync("http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001/plan", null);
            await Assert.That(loaded?.Current).IsFalse();
        }, NullLogger.Instance);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedStoragePlanPublicationKeepsAccountAuthority(bool disconnected)
    {
        using var wire = new Wire { Status = HttpStatusCode.InternalServerError, DisconnectPut = disconnected };
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var loaded = ServerTiming.Now();
            using var client = wire.CreateClient("plan");
            using var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:8081/admin/organizations/01JA0000000000000000000001/plan");
            request.Options.Set(TerraBasePlanSync.Publication, new("hobby"));
            try
            {
                using var response = await client.SendAsync(request);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(disconnected).IsFalse();
            }
            catch (HttpRequestException) when (disconnected) { }
            await Assert.That(loaded?.Current).IsTrue();
        }, NullLogger.Instance);
    }

    [Test]
    public async Task ReadsDuringAWriteCannotBecomeFreshContextsOrCachedPostWriteReads()
    {
        using var wire = new Wire();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Mutating = () => finished.Task;
        await ServerTiming.MiddlewareAsync(new DefaultHttpContext { Request = { Method = "GET" } }, async () =>
        {
            var before = ServerTiming.Now();
            var reads = 0;
            Task<int> Read() => ServerTiming.ReadAsync("membership", () => Task.FromResult(++reads));
            await Read();
            using var client = wire.CreateClient("mutation");
            var pending = client.PostAsync("https://auth.terrascale.tech/v1/membership", null);
            await Assert.That(before?.Current).IsFalse();
            await Assert.That(ServerTiming.Now() is null).IsTrue();
            await Read();
            finished.SetResult();
            using var response = await pending;
            await Read();
            await Read();
            await Assert.That(reads).IsEqualTo(3);
            await Assert.That(before?.Current).IsFalse();
            await Assert.That(ServerTiming.Now()?.Current).IsTrue();
        }, NullLogger.Instance);
    }

    [Test]
    public async Task ResourceNamesThatMatchOperationWordsAreStillRedacted()
    {
        await Assert.That(ServerTiming.Handler.Operation(new("https://api.terrascale.tech/v1/projects/organizations/databases/members/collections/query/documents/userinfo?token=secret")))
            .IsEqualTo("v1/projects/:value/databases/:value/collections/:value/documents/:value");
    }

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
        public Func<Task>? Mutating { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool DisconnectPut { get; set; }
        public HttpClient CreateClient(string name) => new(new ServerTiming.Handler { InnerHandler = this }, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if ((request.Method == HttpMethod.Post || request.Method == HttpMethod.Put) && Mutating is { } mutate) await mutate();
            if (request.Method == HttpMethod.Put && DisconnectPut) throw new HttpRequestException("Synthetic plan connection failure.");
            var catalog = request.RequestUri!.AbsolutePath == "/v1/regions";
            if (catalog) { Catalogs++; await Release.Task.WaitAsync(cancellationToken); }
            else if (request.Method == HttpMethod.Get) Listings++;
            return new(Status)
            {
                Content = new StringContent(catalog ? "{\"regions\":[\"gru\",\"fra\"]}" : "{\"items\":[],\"next\":null}", Encoding.UTF8, "application/json")
            };
        }
    }
}
