using System.Text.Json;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.TerraBaseFixture;

namespace TerraScale.Authentication.Fast.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthStoreTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_missing_conditional_delete_rolls_back_and_preserves_the_operation_index_but_missing_collections_propagate(bool missingCollection)
    {
        var (_, database) = await fixture.CreateDatabaseAsync(provision: false);
        await database.CreateCollectionAsync(AuthStore.MembershipCollection);
        if (!missingCollection)
        {
            await database.CreateCollectionAsync(AuthStore.SessionCollection);
        }

        var store = new AuthStore(database, TimeProvider.System);
        var original = await store.CreateAsync(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"member"}"""));
        StoreOperation[] operations =
        [
            AuthStore.Require(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"eq":["/role","member"]}"""), original.Version),
            AuthStore.Replace(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"admin"}"""), original.Version),
            AuthStore.Delete(AuthStore.SessionCollection, "terrascale", new string('c', 64), original.Version),
        ];
        if (missingCollection)
        {
            await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => store.TransactAsync(operations));
        }
        else
        {
            await Assert.That(await store.TransactAsync(operations)).IsEqualTo(2);
        }

        var after = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/user"))!;
        await Assert.That(after.Version).IsEqualTo(original.Version);
        await Assert.That(after.Document.GetProperty("role").GetString()).IsEqualTo("member");
    }

    [Test]
    public async Task An_assertion_holds_only_at_the_version_its_check_read_when_the_transaction_writes_the_same_document()
    {
        // TS-598: a role checked at one version must not be re-asserted through a write at a later version.
        var (_, database) = await fixture.CreateDatabaseAsync();
        var store = new AuthStore(database, TimeProvider.System);
        await store.CreateAsync(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"admin"}"""));
        var checkedAt = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/user"))!.Version;
        var demoted = (await store.ReplaceAsync(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"viewer"}"""), checkedAt))!;
        var admin = Json("""{"in":["/role",["admin","owner"]]}""");

        // The caller checked "admin" at the first version, then reread the record after the demotion.
        await Assert.That(await store.TransactAsync(
        [
            AuthStore.Require(AuthStore.MembershipCollection, "terrascale", "org/user", admin, checkedAt),
            AuthStore.Replace(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"admin"}"""), demoted),
        ])).IsEqualTo(0);
        await Assert.That((await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/user"))!.Document.GetProperty("role").GetString()).IsEqualTo("viewer");

        // Checked and written at the same version, the write alone says it; an assertion there needs its version.
        await Assert.That(await store.TransactAsync(
        [
            AuthStore.Require(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"in":["/role",["viewer"]]}"""), demoted),
            AuthStore.Replace(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"member"}"""), demoted),
        ])).IsNull();
        var current = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/user"))!.Version;
        await Assert.That(async () => await store.TransactAsync(
        [
            AuthStore.Require(AuthStore.MembershipCollection, "terrascale", "org/user", admin),
            AuthStore.Replace(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"member"}"""), current),
        ])).Throws<ArgumentException>();

        // A server-side failure after an omitted assertion names the caller's operation, not the one sent, and nothing
        // commits: here the claim (index 3) is already taken, so the write and the create before it roll back.
        await Assert.That(await store.TransactAsync([AuthStore.Claim("terrascale", "test-name", "taken", "owner-a")])).IsNull();
        await Assert.That(await store.TransactAsync(
        [
            AuthStore.Require(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"in":["/role",["member"]]}"""), current),
            AuthStore.Replace(AuthStore.MembershipCollection, "terrascale", "org/user", Json("""{"role":"admin"}"""), current),
            AuthStore.Create(AuthStore.MembershipCollection, "terrascale", "org/other", Json("""{"role":"viewer"}""")),
            AuthStore.Claim("terrascale", "test-name", "taken", "owner-b"),
        ])).IsEqualTo(3);
        await Assert.That((await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/user"))!.Document.GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", "org/other")).IsNull();

        static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    }

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
            await Assert.That(TerraScaleIds.IsValid(document.Id)).IsTrue();
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
        using (var http = TerraBaseFixture.NewHttpClient())
        {
            var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = fixture.Server.ClientAddress, ApiKey = tenant.ApiKey });
            var store = new AuthStore(client.Database(tenant.Project.Id, "auth"), TimeProvider.System);
            version = (await store.CreateAsync("items", "tenant", "persistent", Json("""{"value":"durable"}"""))).Version;
            await Assert.That(await store.ClaimUniqueAsync("tenant", "email", "durable@example.test", "owner")).IsTrue();
        }

        using var restartedHttp = TerraBaseFixture.NewHttpClient();
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
            .IsEquivalentTo(["account_deletion_receipts", "api_key_usage", "api_keys", "audit_events", "closure_cancellations", "closure_commits", "closure_jobs", "dashboard_deletion_cursors", "deletion_jobs", "email_admission", "email_alert_deliveries", "email_allowance_deliveries", "email_config_versions", "email_configs", "email_support_deliveries", "history_erasure_marks", "invitations", "memberships", "mfa_credentials", "oidc_applications", "oidc_authorizations", "oidc_tokens", "organization_sso", "organizations", "passcodes", "project_auth_settings", "project_members", "projects", "rate_limits", "sessions", "signing_keys", "step_up_deletion_grants", "step_up_passcodes", "step_up_proofs", "storage_health", "uniques", "users", "verification_tokens"]);

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
