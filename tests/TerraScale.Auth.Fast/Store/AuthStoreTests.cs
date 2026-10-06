using System.Text.Json;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Auth.Tests.TerraBaseFixture;

namespace TerraScale.Auth.Fast.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthStoreTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Canonical_email_has_one_durable_owner_across_independent_stores()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var first = new AuthStore(database, TimeProvider.System);
        var second = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
        var claims = await Task.WhenAll(
            first.ClaimUniqueAsync("instance", "email", AuthStore.NormalizeEmail("  Alice@Example.TEST  "), "owner-a"),
            second.ClaimUniqueAsync("instance", "email", AuthStore.NormalizeEmail("alice@example.test"), "owner-b"));

        await Assert.That(claims.Count(won => won)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(1);
        var winner = claims[0] ? "owner-a" : "owner-b";
        var loser = claims[0] ? "owner-b" : "owner-a";
        await Assert.That(await second.ReleaseUniqueAsync("instance", "email", "alice@example.test", loser)).IsFalse();
        await Assert.That(await first.ClaimUniqueAsync("instance", "email", "alice@example.test", loser)).IsFalse();
        await Assert.That(await second.ReleaseUniqueAsync("instance", "email", "alice@example.test", winner)).IsTrue();
        await Assert.That(await first.ClaimUniqueAsync("instance", "email", "alice@example.test", loser)).IsTrue();
        await Assert.That(await second.ReleaseUniqueAsync("instance", "email", "alice@example.test", winner)).IsFalse();
    }

    [Test]
    [Arguments("tenant-a", "email", "same", "tenant-b", "email", "same")]
    [Arguments("tenant", "email", "same", "tenant", "username", "same")]
    [Arguments("a:b", "c", "d", "a", "b:c", "d")]
    [Arguments("a|b", "c", "d", "a", "b|c", "d")]
    [Arguments("a", "b", "c|d", "a", "b|c", "d")]
    public async Task Scoped_natural_keys_do_not_collide(
        string instanceA, string kindA, string keyA, string instanceB, string kindB, string keyB)
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var store = new AuthStore(database, TimeProvider.System);

        await Assert.That(await store.ClaimUniqueAsync(instanceA, kindA, keyA, "owner-a")).IsTrue();
        await Assert.That(await store.ClaimUniqueAsync(instanceB, kindB, keyB, "owner-b")).IsTrue();
        var documents = (await database.ListDocumentsAsync("uniques")).Items;
        await Assert.That(documents.Count).IsEqualTo(2);
        foreach (var document in documents)
        {
            var bytes = Guid.Parse(document.Id).ToByteArray(bigEndian: true);
            await Assert.That(bytes[6] >> 4).IsEqualTo(8);
            await Assert.That(bytes[8] >> 6).IsEqualTo(2);
        }

        await Assert.That(await store.ReleaseUniqueAsync(instanceA, kindA, keyA, "owner-a")).IsTrue();
        await Assert.That(await store.ClaimUniqueAsync(instanceB, kindB, keyB, "owner-c")).IsFalse();
    }

    [Test]
    public async Task Create_only_and_versions_reject_stale_updates_and_deletes()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await database.CreateCollectionAsync("items");
        var first = new AuthStore(database, TimeProvider.System);
        var second = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);

        await Assert.That(await first.GetAsync("items", "tenant-a", "item")).IsNull();
        var created = await first.CreateAsync("items", "tenant-a", "item", Json("""{"value":1}"""));
        await Assert.That(created.Created).IsTrue();
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => second.CreateAsync("items", "tenant-a", "item", Json("""{"value":99}""")));
        var original = await second.GetAsync("items", "tenant-a", "item");
        await Assert.That(original!.Version).IsEqualTo(created.Version);
        await Assert.That(original.Document.GetProperty("value").GetInt32()).IsEqualTo(1);
        await Assert.That(await second.GetAsync("items", "tenant-b", "item")).IsNull();
        await Assert.That(await first.CompareAndPutAsync("items", "tenant-a", "item", Json("""{"value":2}"""), original.Version)).IsTrue();
        await Assert.That(await second.CompareAndPutAsync("items", "tenant-a", "item", Json("""{"value":99}"""), original.Version)).IsFalse();
        await Assert.That(await second.CompareAndDeleteAsync("items", "tenant-a", "item", original.Version)).IsFalse();

        var current = await second.GetAsync("items", "tenant-a", "item");
        await Assert.That(current!.Version).IsNotEqualTo(original.Version);
        await Assert.That(current.Document.GetProperty("value").GetInt32()).IsEqualTo(2);
        await Assert.That(await second.CompareAndPutAsync("items", "tenant-a", "item", Json("""{"value":3}"""), current.Version)).IsTrue();
        var refreshed = await first.GetAsync("items", "tenant-a", "item");
        await Assert.That(refreshed!.Version).IsNotEqualTo(current.Version);
        await Assert.That(await first.CompareAndDeleteAsync("items", "tenant-a", "item", refreshed.Version)).IsTrue();
        await Assert.That(await second.GetAsync("items", "tenant-a", "item")).IsNull();
    }

    [Test]
    public async Task Recreated_client_reads_state_committed_by_a_disposed_client()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await database.CreateCollectionAsync("items");
        string version;
        using (var http = fixture.NewHttpClient())
        {
            var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = fixture.Server.ClientAddress, ApiKey = tenant.ApiKey });
            var store = new AuthStore(client.Database(tenant.Project.Id, "auth"), TimeProvider.System);
            version = (await store.CreateAsync("items", "tenant", "persistent", Json("""{"value":"durable"}"""))).Version;
            await Assert.That(await store.ClaimUniqueAsync("tenant", "email", "durable@example.test", "owner")).IsTrue();
        }

        using var restartedHttp = fixture.NewHttpClient();
        var restartedClient = new TerraBaseDocumentsClient(restartedHttp, new TerraBaseDocumentsClientOptions { BaseAddress = fixture.Server.ClientAddress, ApiKey = tenant.ApiKey });
        var restartedStore = new AuthStore(restartedClient.Database(tenant.Project.Id, "auth"), TimeProvider.System);
        var read = await restartedStore.GetAsync("items", "tenant", "persistent");
        await Assert.That(read!.Version).IsEqualTo(version);
        await Assert.That(read.Document.GetProperty("value").GetString()).IsEqualTo("durable");
        await Assert.That(await restartedStore.ClaimUniqueAsync("tenant", "email", "durable@example.test", "another-owner")).IsFalse();
    }

    [Test]
    public async Task Provisioning_is_idempotent_and_requires_an_existing_database()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync(provision: false);
        var store = new AuthStore(database, TimeProvider.System);
        await store.ProvisionAsync();
        await store.ProvisionAsync();
        await Assert.That((await database.ListCollectionsAsync()).Items.Select(collection => collection.Name))
            .IsEquivalentTo(["oidc_applications", "oidc_authorizations", "oidc_tokens", "passcodes", "rate_limits", "sessions", "signing_key_rings", "storage_health", "uniques", "users", "verification_tokens"]);

        var missing = fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "missing");
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => new AuthStore(missing, TimeProvider.System).ProvisionAsync());
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => missing.ListCollectionsAsync());
    }

    [Test]
    [Arguments("uniques")]
    [Arguments("verification_tokens")]
    [Arguments("storage_health")]
    public async Task Generic_writes_cannot_bypass_dedicated_infrastructure_operations(string collection)
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var key = new string('c', 64);
        var id = AuthStore.NaturalKeyId("tenant", collection, key);
        switch (collection)
        {
            case "uniques":
                await Assert.That(await store.ClaimUniqueAsync("tenant", collection, key, "owner")).IsTrue();
                break;
            case "verification_tokens":
                await store.CreateChallengeAsync("tenant", collection, key, "subject", clock.UtcNow.AddMinutes(5));
                await Assert.That(await store.ConsumeChallengeAsync("tenant", collection, key, "subject")).IsNotNull();
                break;
            default:
                await database.PutDocumentAsync(collection, id, Json("""{"probe":true}"""), TerraBasePrecondition.Absent);
                break;
        }

        var before = await database.GetDocumentAsync(collection, id);
        var bypass = Json("""{"instanceId":"tenant","ownerId":"attacker","consumed":false}""");
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(collection, "tenant", "another-key", bypass));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompareAndPutAsync(collection, "tenant", key, bypass, before!.Version));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompareAndDeleteAsync(collection, "tenant", key, before!.Version));

        var after = await database.GetDocumentAsync(collection, id);
        await Assert.That(after!.Version).IsEqualTo(before!.Version);
        await Assert.That(JsonElement.DeepEquals(after.Document, before.Document)).IsTrue();
        await Assert.That((await database.ListDocumentsAsync(collection)).Items.Count).IsEqualTo(1);
        if (collection == "uniques")
        {
            await Assert.That(await store.ClaimUniqueAsync("tenant", collection, key, "attacker")).IsFalse();
            await Assert.That(await store.ReleaseUniqueAsync("tenant", collection, key, "attacker")).IsFalse();
        }
        else if (collection == "verification_tokens")
        {
            await Assert.That(await store.ConsumeChallengeAsync("tenant", collection, key, "subject")).IsNull();
            await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => store.CreateChallengeAsync("tenant", collection, key, "subject", clock.UtcNow.AddHours(1)));
        }
    }

    [Test]
    public async Task A_claimed_create_writes_neither_document_when_either_address_is_taken()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var store = new AuthStore(database, TimeProvider.System);
        await store.CreateAsync("users", "tenant", "owner", Json("""{"original":true}"""));
        var before = (await store.GetAsync("users", "tenant", "owner"))!;

        // The owner address is taken: the claim (operation 0) must not survive the failed transaction.
        var ownerTaken = await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() =>
            store.CreateClaimedAsync("users", "tenant", "owner", Json("""{"replacement":true}"""), "email", "fresh@example.test"));
        await Assert.That(ownerTaken!.OperationIndex).IsEqualTo(1);
        await Assert.That(await store.FindUniqueOwnerAsync("tenant", "email", "fresh@example.test")).IsNull();
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(0);
        var after = (await store.GetAsync("users", "tenant", "owner"))!;
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).Contains("original");

        // The claim is taken: the new owner record must not be written.
        await Assert.That(await store.CreateClaimedAsync("users", "tenant", "first", Json("""{"n":1}"""), "email", "taken@example.test")).IsTrue();
        await Assert.That(await store.CreateClaimedAsync("users", "tenant", "second", Json("""{"n":2}"""), "email", "taken@example.test")).IsFalse();
        await Assert.That(await store.GetAsync("users", "tenant", "second")).IsNull();
        await Assert.That(await store.FindUniqueOwnerAsync("tenant", "email", "taken@example.test")).IsEqualTo("first");
    }
}
