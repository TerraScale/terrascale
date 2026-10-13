using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// ts-auth reads GET and HEAD requests from its own region's TerraBase (another region's when it does not answer) and
/// sends everything else, every conditional write first, to the designated region's, which it never replaces: without it
/// those operations fail closed. Regions are relays to the suite's TerraBase; the relays show where ts-auth connected, and
/// two databases (<c>auth</c>, the designated copy; <c>auth-local</c>, this region's) show what each copy holds.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageTests(TerraBaseFixture fixture)
{
    private const string Instance = "regional-instance";
    private const string Local = "auth-local";

    [Test]
    public async Task Get_requests_read_their_own_region_and_everything_else_uses_the_designated_one()
    {
        var (tenant, designated) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        await UsersIn(local).CreateAsync(Instance, "local-only@example.test");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other, gru.Address);

        using var created = await auth.Client.PostAsync("/__test/users/created@example.test", null);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await UsersIn(designated).FindByEmailAsync(Instance, "created@example.test")).IsNotNull();
        await Assert.That(await UsersIn(local).FindByEmailAsync(Instance, "created@example.test")).IsNull();

        // This region's copy answers a GET; a document it lacks (not replicated yet) comes from the designated region.
        await Assert.That(await StatusAsync(auth, HttpMethod.Get, "local-only@example.test")).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(auth, HttpMethod.Get, "created@example.test")).IsEqualTo(HttpStatusCode.OK);
        // Any other request reads the designated region's copy, which lacks the local-only user.
        await Assert.That(await StatusAsync(auth, HttpMethod.Post, "local-only@example.test")).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(own.Accepted).IsGreaterThan(0);
        await Assert.That(other.Accepted).IsEqualTo(0);
    }

    [Test]
    public async Task While_the_designated_region_is_down_writes_fail_closed_and_get_reads_go_on()
    {
        var (tenant, designated) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        await UsersIn(local).CreateAsync(Instance, "local-only@example.test");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other, gru.Address);

        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
        gru.Stop();
        using var refused = await auth.Client.PostAsync("/__test/users/new@example.test", null);

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
        await Assert.That(await UsersIn(local).FindByEmailAsync(Instance, "new@example.test")).IsNull();
        await Assert.That(await UsersIn(designated).FindByEmailAsync(Instance, "new@example.test")).IsNull();
        await Assert.That(await StatusAsync(auth, HttpMethod.Get, "local-only@example.test")).IsEqualTo(HttpStatusCode.OK);
        // Ready means the designated region takes writes.
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(15))).IsTrue();
    }

    [Test]
    [Arguments("Provision")]
    [Arguments("Create")]
    [Arguments("CompareAndPut")]
    [Arguments("Replace")]
    [Arguments("ReplaceClaimed")]
    [Arguments("CommitVersion")]
    [Arguments("CompareAndDelete")]
    [Arguments("ClaimUnique")]
    [Arguments("CreateClaimed")]
    [Arguments("ReleaseUnique")]
    [Arguments("Transact")]
    [Arguments("CreateChallenge")]
    [Arguments("ConsumeChallenge")]
    [Arguments("Get")]
    [Arguments("GetForWrite")]
    [Arguments("FindUniqueOwner")]
    [Arguments("FindUnique")]
    [Arguments("Query")]
    [Arguments("QueryEquals")]
    [Arguments("Page")]
    public async Task Outside_get_requests_every_storage_call_fails_closed_while_the_designated_region_is_down(string operation)
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        // This region's copy holds what every operation needs, so one sent there would succeed.
        var version = await SeedAsync(local);
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other, gru.Address);
        var store = auth.App.Services.GetRequiredService<AuthStore>();
        gru.Stop();
        var before = await ContentsAsync(local);

        var failure = await Assert.ThrowsAsync<ApiException>(() => RunAsync(store, operation, version));

        await Assert.That(failure!.Status).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
        await Assert.That(failure.Code).IsEqualTo("temporarily_unavailable");
        await Assert.That(await ContentsAsync(local)).IsEquivalentTo(before);
    }

    [Test]
    [Arguments("Create")]
    [Arguments("CompareAndPut")]
    [Arguments("Replace")]
    [Arguments("ReplaceClaimed")]
    [Arguments("CommitVersion")]
    [Arguments("CompareAndDelete")]
    [Arguments("ClaimUnique")]
    [Arguments("CreateClaimed")]
    [Arguments("ReleaseUnique")]
    [Arguments("Transact")]
    [Arguments("CreateChallenge")]
    [Arguments("ConsumeChallenge")]
    public async Task Every_write_lands_in_the_designated_copy_only(string operation)
    {
        var (tenant, designated) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        // Both copies hold what every write needs; versions name the designated copy's documents.
        await SeedAsync(local);
        var version = await SeedAsync(designated);
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other, gru.Address);
        var store = auth.App.Services.GetRequiredService<AuthStore>();
        var (localBefore, designatedBefore) = (await ContentsAsync(local), await ContentsAsync(designated));

        var succeeded = await RunAsync(store, operation, version) switch
        {
            bool done => done,
            int => false,
            null => operation is "Transact" or "CreateChallenge",
            _ => true,
        };

        await Assert.That(succeeded).IsTrue();
        await Assert.That(await ContentsAsync(local)).IsEquivalentTo(localBefore);
        await Assert.That(await ContentsAsync(designated)).IsNotEquivalentTo(designatedBefore);
    }

    [Test]
    public async Task Get_reads_move_to_another_region_when_their_own_stops_answering()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        await UsersIn(local).CreateAsync(Instance, "local-only@example.test");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other, gru.Address);
        await Assert.That(await StatusAsync(auth, HttpMethod.Get, "local-only@example.test")).IsEqualTo(HttpStatusCode.OK);

        own.Stop();

        // A request on the dropped connection fails with it (503); the next connection goes to the other region.
        await Assert.That(await ReadinessWait.UntilAsync(async () => await StatusAsync(auth, HttpMethod.Get, "local-only@example.test") == HttpStatusCode.OK && other.Accepted > 0,
            TimeSpan.FromSeconds(15))).IsTrue();
    }

    [Test]
    public async Task Get_reads_use_another_region_when_their_own_has_no_started_machine()
    {
        // Fly lists started machines only, so a region without one does not resolve; neither does this name off Fly.
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, Local);
        await UsersIn(local).CreateAsync(Instance, "local-only@example.test");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, new Uri($"http://sjc.ts-auth-test-{Guid.NewGuid():N}.internal:8081/"), other, gru.Address);

        await Assert.That(await StatusAsync(auth, HttpMethod.Get, "local-only@example.test")).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(other.Accepted).IsGreaterThan(0);
    }

    private static readonly JsonElement Record = TerraBaseFixture.Json($$"""{"instanceId":"{{Instance}}"}""");

    private static readonly string Hash = new('a', 64);

    private static Users UsersIn(TerraBaseDatabase copy) => new(new AuthStore(copy, TimeProvider.System), TimeProvider.System);

    // A user document, a unique claim and a challenge, which the operations below need; returns the user's version.
    private static async Task<string> SeedAsync(TerraBaseDatabase copy)
    {
        var store = new AuthStore(copy, TimeProvider.System);
        var version = (await store.CreateAsync(AuthStore.UserCollection, Instance, "seeded", Record)).Version;
        await store.ClaimUniqueAsync(Instance, "email", "seeded@example.test", "seeded");
        await store.CreateChallengeAsync(Instance, "verify", Hash, "seeded", DateTimeOffset.UtcNow.AddHours(1));
        return version;
    }

    // The operation's result: what its method returns, or null for one that returns nothing.
    private static async Task<object?> RunAsync(AuthStore store, string operation, string version) => operation switch
    {
        "Provision" => await Done(store.ProvisionAsync()),
        "Create" => await store.CreateAsync(AuthStore.UserCollection, Instance, "fresh", Record),
        "CompareAndPut" => await store.CompareAndPutAsync(AuthStore.UserCollection, Instance, "seeded", Record, version),
        "Replace" => await store.ReplaceAsync(AuthStore.UserCollection, Instance, "seeded", Record, version),
        "ReplaceClaimed" => await store.ReplaceClaimedAsync(AuthStore.UserCollection, Instance, "seeded", Record, version, "email", "fresh@example.test"),
        "CommitVersion" => await store.CommitVersionAsync("email_configs", Instance, "fresh", Record, null, "email_config_versions", "fresh"),
        "CompareAndDelete" => await store.CompareAndDeleteAsync(AuthStore.UserCollection, Instance, "seeded", version),
        "ClaimUnique" => await store.ClaimUniqueAsync(Instance, "email", "fresh@example.test", "fresh"),
        "CreateClaimed" => await store.CreateClaimedAsync(AuthStore.UserCollection, Instance, "fresh", Record, "email", "fresh@example.test"),
        "ReleaseUnique" => await store.ReleaseUniqueAsync(Instance, "email", "seeded@example.test", "seeded"),
        "Transact" => await store.TransactAsync([AuthStore.Create(AuthStore.UserCollection, Instance, "fresh", Record)]),
        "CreateChallenge" => await Done(store.CreateChallengeAsync(Instance, "verify", new string('b', 64), "fresh", DateTimeOffset.UtcNow.AddHours(1))),
        "ConsumeChallenge" => await store.ConsumeChallengeAsync(Instance, "verify", Hash, "seeded"),
        "Get" => await store.GetAsync(AuthStore.UserCollection, Instance, "seeded"),
        "GetForWrite" => await store.GetForWriteAsync(AuthStore.UserCollection, Instance, "seeded"),
        "FindUniqueOwner" => await store.FindUniqueOwnerAsync(Instance, "email", "seeded@example.test"),
        "FindUnique" => await store.FindUniqueAsync(Instance, "email", "seeded@example.test"),
        "Query" => await store.QueryAsync(AuthStore.UserCollection, TerraBaseFixture.Json($$"""{"eq":["/instanceId","{{Instance}}"]}"""), 10),
        "QueryEquals" => await store.QueryAsync(AuthStore.UserCollection, Instance, "/instanceId", Instance).ToListAsync().AsTask(),
        "Page" => await store.PageAsync(AuthStore.UserCollection, Instance, [], [], 10, null),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static async Task<object?> Done(Task task)
    {
        await task;
        return null;
    }

    // Every document of the copy, as collection, id and version.
    private static async Task<List<string>> ContentsAsync(TerraBaseDatabase copy)
    {
        var contents = new List<string>();
        foreach (var collection in AuthStore.RequiredCollections)
        {
            contents.AddRange((await copy.ListDocumentsAsync(collection)).Items.Select(item => $"{collection}/{item.Id}/{item.Version}"));
        }

        return contents;
    }

    private static async Task<HttpStatusCode> StatusAsync(AuthTestServer auth, HttpMethod method, string email)
    {
        using var request = new HttpRequestMessage(method, method == HttpMethod.Get ? $"/__test/users/{email}" : $"/__test/users/{email}/find");
        using var response = await auth.Client.SendAsync(request);
        return response.StatusCode;
    }

    private static Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant, Uri ownRegion, TcpRelay other, Uri designatedRegion) =>
        AuthTestServer.StartAsync(
            services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, ownRegion, other, designatedRegion, localDatabase: Local),
            // The operator's signing-key provisioning runs first, so /readyz shows whether the designated region takes writes.
            beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None),
            routes: app =>
            {
                app.MapGet("/__test/users/{email}", FindAsync);
                app.MapPost("/__test/users/{email}/find", FindAsync);
                app.MapPost("/__test/users/{email}", async (string email, Users users) =>
                    await users.CreateAsync(Instance, email) is not null ? Results.StatusCode(StatusCodes.Status201Created) : Results.Conflict());
            });

    private static async Task<IResult> FindAsync(string email, Users users) =>
        await users.FindByEmailAsync(Instance, email) is not null ? Results.Ok() : Results.NotFound();
}
