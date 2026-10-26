using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// TBS1 sealing and the shape key endpoint (docs/terrabase/shape-reads.md sections 12, 13 and 16) on one real node per
/// test: the production <see cref="IShapeCrypto"/> from DI over the real <see cref="DataKeys"/>, backed by the in-memory
/// bucket holding a frozen wrapped project key. Every object is opened by the strict client decoder below, written from
/// the specification alone; the shape lineage is the only fixture, so the endpoint has a current handle to check.
/// </summary>
public sealed class ShapeCryptoTests
{
    private const int FrameBytes = 1 << 20;
    private const int MaxPlainBytes = 8 * FrameBytes;
    private const int HeaderBytes = 70;
    private const int TagBytes = 16;
    private const uint LastFrame = 1u << 31;
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ReadScopes = ["documents:read"];
    // TUnit builds one instance per test, so each test owns its generator.
    private readonly Faker _faker = new();

    [Test]
    public async Task The_key_endpoint_releases_the_current_epoch_key_derived_from_the_frozen_project_key()
    {
        await using var host = await CryptoHost.StartAsync();

        using var response = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, host.KeyId);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl?.Private).IsTrue();
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(string.Join(',', body.EnumerateObject().Select(property => property.Name))).IsEqualTo("handle,epoch,key_id,key");
        await Assert.That(body.GetProperty("handle").GetString()).IsEqualTo(host.Handle);
        await Assert.That(body.GetProperty("epoch").GetString()).IsEqualTo(Decimal(host.Epoch));
        await Assert.That(body.GetProperty("key_id").GetString()).IsEqualTo(host.KeyId);
        var released = body.GetProperty("key").GetString()!;
        await Assert.That(released.Contains('=', StringComparison.Ordinal)).IsFalse();
        await Assert.That(Convert.ToHexStringLower(Base64Url.DecodeFromChars(released)))
            .IsEqualTo(Convert.ToHexStringLower(Client.EpochKey(host.DataKey, host.Handle, host.Epoch)));
        // K_e, never K_d.
        await Assert.That(released).IsNotEqualTo(Base64Url.EncodeToString(host.DataKey));
    }

    [Test]
    public async Task A_filtered_handle_releases_its_epoch_key_until_its_collection_is_recreated()
    {
        const string where = """{"eq":["/status","open"]}""";
        // The real lineage resolves the filtered handle, so the key endpoint needs its registry entry and filter admission.
        await using var host = await CryptoHost.StartAsync(services: collection =>
            collection.AddSingleton<IShapeLineages>(provider => provider.GetRequiredService<ShapeService>()));
        await Assert.That((await host.Api.PutAsync(DocumentApi.NewId(), new { status = "open" })).Status).IsEqualTo(HttpStatusCode.Created);

        using var bootstrap = await host.Api.Client.GetAsync(new Uri(
            $"{host.Api.Collections}/{DocumentApi.Collection}/shape?offset=-1&where={Uri.EscapeDataString(where)}", UriKind.Relative));
        await Assert.That(bootstrap.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        await Assert.That(handle).IsNotEqualTo(host.Handle);

        using var released = await host.GetKeyAsync(host.Api.Client, handle, host.Epoch, host.KeyId);
        await Assert.That(released.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var key = Base64Url.DecodeFromChars((await released.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
        await Assert.That(key.AsSpan().SequenceEqual(Client.EpochKey(host.DataKey, handle, host.Epoch))).IsTrue();

        var plain = _faker.Random.Bytes(64);
        using var sealedObject = await host.Crypto.EncryptAsync(await host.Api.TenantOfAsync(), handle, "2", plain, CancellationToken.None);
        await Assert.That(Client.Open(sealedObject.Bytes.ToArray(), handle, "2", host.Epoch, host.KeyId, key)?.AsSpan().SequenceEqual(plain)).IsTrue();

        await Assert.That((await host.Api.SendAsync(HttpMethod.Delete, host.Api.Collections + "/" + DocumentApi.Collection)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await host.Api.SendAsync(HttpMethod.Post, host.Api.Collections, new { name = DocumentApi.Collection })).Status).IsEqualTo(HttpStatusCode.Created);

        using var stale = await host.GetKeyAsync(host.Api.Client, handle, host.Epoch, host.KeyId);
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stale.Headers.CacheControl?.NoStore).IsTrue();
        var refusal = await stale.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(refusal.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("must_refetch");
        var current = stale.Headers.GetValues("TB-Shape-Handle").Single();
        await Assert.That(current).IsNotEqualTo(handle);
        await Assert.That(refusal.GetProperty("error").GetProperty("handle").GetString()).IsEqualTo(current);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(FrameBytes - 1)]
    [Arguments(FrameBytes)]
    [Arguments(2 * FrameBytes + 3)]
    public async Task A_sealed_object_opens_with_the_released_key_and_only_for_its_own_handle_offset_and_epoch(int length)
    {
        await using var host = await CryptoHost.StartAsync();
        var offset = _faker.Random.Bool() ? Decimal(_faker.Random.Long(1, 1_000_000)) : $"snap.{_faker.Random.Long(0, 1000)}.0";
        var plain = _faker.Random.Bytes(length);
        var key = await host.ReleasedKeyAsync(host.Epoch, host.KeyId);

        using var sealedObject = await host.Crypto.EncryptAsync(await host.Api.TenantOfAsync(), host.Handle, offset, plain, CancellationToken.None);
        var bytes = sealedObject.Bytes.ToArray();

        await Assert.That(sealedObject.Epoch).IsEqualTo(host.Epoch);
        await Assert.That(sealedObject.KeyId).IsEqualTo(host.KeyId);
        await Assert.That(bytes.Length).IsEqualTo(HeaderBytes + (length / FrameBytes + 1) * (4 + TagBytes) + length);
        await Assert.That(Client.Open(bytes, host.Handle, offset, host.Epoch, host.KeyId, key)?.AsSpan().SequenceEqual(plain)).IsTrue();
        var otherHandle = Handle();
        await Assert.That(Client.Open(bytes, otherHandle, offset, host.Epoch, host.KeyId, key)).IsNull();
        await Assert.That(Client.Open(bytes, host.Handle, offset + "0", host.Epoch, host.KeyId, key)).IsNull();
        await Assert.That(Client.Open(bytes, host.Handle, offset, host.Epoch - 1, host.KeyId, key)).IsNull();
        await Assert.That(Client.Open(bytes, host.Handle, offset, host.Epoch, host.KeyId, Client.EpochKey(host.DataKey, host.Handle, host.Epoch + 1))).IsNull();
        await Assert.That(Client.Open(bytes, host.Handle, offset, host.Epoch, host.KeyId, Client.EpochKey(host.DataKey, otherHandle, host.Epoch))).IsNull();
        var flipped = bytes.ToArray();
        flipped[_faker.Random.Int(0, flipped.Length - 1)] ^= (byte)(1 << _faker.Random.Int(0, 7));
        await Assert.That(Client.Open(flipped, host.Handle, offset, host.Epoch, host.KeyId, key)).IsNull();
    }

    [Test]
    public async Task Every_object_gets_a_fresh_salt_so_no_object_key_and_nonce_pair_repeats()
    {
        await using var host = await CryptoHost.StartAsync();
        var database = await host.Api.TenantOfAsync();
        var plain = Encoding.UTF8.GetBytes(_faker.Lorem.Paragraph());
        var key = await host.ReleasedKeyAsync(host.Epoch, host.KeyId);
        var salts = new HashSet<string>(StringComparer.Ordinal);
        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        var ciphertexts = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 32; index++)
        {
            using var sealedObject = await host.Crypto.EncryptAsync(database, host.Handle, "2", plain, CancellationToken.None);
            var bytes = sealedObject.Bytes.ToArray();
            var salt = bytes.AsSpan(38, 32).ToArray();
            salts.Add(Convert.ToHexStringLower(salt));
            objectKeys.Add(Convert.ToHexStringLower(Client.ObjectKey(key, salt, host.Handle, "2")));
            ciphertexts.Add(Convert.ToHexStringLower(bytes.AsSpan(HeaderBytes)));
            await Assert.That(Client.Open(bytes, host.Handle, "2", host.Epoch, host.KeyId, key)?.AsSpan().SequenceEqual(plain)).IsTrue();
        }

        // Every object uses nonce 0 for its only frame, so distinct object keys are what keep (key, nonce) unique.
        await Assert.That(salts.Count).IsEqualTo(32);
        await Assert.That(objectKeys.Count).IsEqualTo(32);
        await Assert.That(ciphertexts.Count).IsEqualTo(32);
    }

    [Test]
    public async Task The_epoch_rotates_on_the_ten_minute_clock_and_new_objects_never_use_the_old_epoch()
    {
        await using var host = await CryptoHost.StartAsync();
        var database = await host.Api.TenantOfAsync();
        var plain = Encoding.UTF8.GetBytes(_faker.Lorem.Sentence());

        host.Time.Advance(TimeSpan.FromSeconds(599));
        var sameEpoch = host.Crypto.CurrentEpoch;
        using var late = await host.Crypto.EncryptAsync(database, host.Handle, "4", plain, CancellationToken.None);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        using var next = await host.Crypto.EncryptAsync(database, host.Handle, "4", plain, CancellationToken.None);
        using var current = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch + 1, host.KeyId);
        using var previous = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, host.KeyId);
        using var future = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch + 2, host.KeyId);

        await Assert.That(sameEpoch).IsEqualTo(host.Epoch);
        await Assert.That(late.Epoch).IsEqualTo(host.Epoch);
        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(late.Bytes.Span[4..])).IsEqualTo((ulong)host.Epoch);
        await Assert.That(host.Crypto.CurrentEpoch).IsEqualTo(host.Epoch + 1);
        await Assert.That(next.Epoch).IsEqualTo(host.Epoch + 1);
        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(next.Bytes.Span[4..])).IsEqualTo((ulong)(host.Epoch + 1));
        await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var key = Base64Url.DecodeFromChars((await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
        await Assert.That(Client.Open(next.Bytes.ToArray(), host.Handle, "4", host.Epoch + 1, host.KeyId, key)?.AsSpan().SequenceEqual(plain)).IsTrue();
        await Assert.That(Client.Open(late.Bytes.ToArray(), host.Handle, "4", host.Epoch, host.KeyId, key)).IsNull();
        // No retained object names the previous epoch, so it is no longer released.
        await AssertRefetchAsync(previous, host.Handle);
        await AssertInvalidAsync(future);
    }

    [Test]
    public async Task Sealing_that_crosses_an_epoch_boundary_is_discarded_and_the_retry_seals_under_the_new_epoch()
    {
        RotatingBudget? budget = null;
        await using var host = await CryptoHost.StartAsync(services => services.AddSingleton<IShapeCrypto>(provider =>
        {
            budget = new RotatingBudget(provider.GetRequiredService<IMemoryBudget>(), (FakeTimeProvider)provider.GetRequiredService<TimeProvider>());
            return new ShapeCrypto(provider.GetService<DataKeys>(), provider.GetRequiredService<IShapeKeyRetention>(), budget, provider.GetRequiredService<TimeProvider>());
        }));
        var database = await host.Api.TenantOfAsync();
        var plain = Encoding.UTF8.GetBytes(_faker.Lorem.Paragraph());
        _ = host.Crypto;
        budget!.RotateOnNextReserve = true;

        var crossed = await Assert.ThrowsAsync<IOException>(() => host.Crypto.EncryptAsync(database, host.Handle, "2", plain, CancellationToken.None));
        using var retried = await host.Crypto.EncryptAsync(database, host.Handle, "2", plain, CancellationToken.None);

        await Assert.That(crossed).IsNotNull();
        await Assert.That(budget.Outstanding).IsEqualTo(1);
        await Assert.That(retried.Epoch).IsEqualTo(host.Epoch + 1);
        await Assert.That(Client.Open(retried.Bytes.ToArray(), host.Handle, "2", host.Epoch + 1, host.KeyId, await host.ReleasedKeyAsync(host.Epoch + 1, host.KeyId))
            ?.AsSpan().SequenceEqual(plain)).IsTrue();
    }

    [Test]
    public async Task Malformed_stale_and_future_parameters_are_refused_without_releasing_a_key()
    {
        await using var host = await CryptoHost.StartAsync();
        var epoch = Decimal(host.Epoch);
        string[] invalid =
        [
            "",
            $"?handle={host.Handle}&epoch={epoch}",
            $"?handle={host.Handle}&key_id={host.KeyId}",
            $"?epoch={epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch={epoch}&key_id={host.KeyId}&extra=1",
            $"?handle={host.Handle}&handle={host.Handle}&epoch={epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch=0{epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch=%2B{epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch=-1&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch=%20{epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch={epoch}.0&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch=99999999999999999999&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch={Decimal(host.Epoch + 1)}&key_id={host.KeyId}",
            $"?handle={host.Handle}&epoch={epoch}&key_id={host.KeyId.ToLowerInvariant()}",
            $"?handle={host.Handle}&epoch={epoch}&key_id={host.KeyId[..25]}",
            $"?handle={host.Handle.ToUpperInvariant()}&epoch={epoch}&key_id={host.KeyId}",
            $"?handle={host.Handle[..18]}&epoch={epoch}&key_id={host.KeyId}",
            $"?handle=h2-{host.Handle[3..]}&epoch={epoch}&key_id={host.KeyId}",
        ];

        foreach (var query in invalid)
        {
            using var response = await host.Api.Client.GetAsync(new Uri(host.Keys + query, UriKind.Relative));
            await AssertInvalidAsync(response);
        }

        using var stale = await host.GetKeyAsync(host.Api.Client, Handle(), host.Epoch, host.KeyId);
        using var unknownKey = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, EntityIdsAt(Start.AddMinutes(-2)));
        await AssertRefetchAsync(stale, host.Handle);
        await AssertRefetchAsync(unknownKey, host.Handle);
    }

    [Test]
    public async Task The_key_needs_documents_read_on_the_client_listener_and_the_tenants_own_collection()
    {
        await using var host = await CryptoHost.StartAsync();
        var query = $"?handle={host.Handle}&epoch={Decimal(host.Epoch)}&key_id={host.KeyId}";
        using var writeOnly = await host.Api.ClientWithScopesAsync("documents:write");
        using var anonymous = host.Api.Management.DataClient();
        using var stranger = await host.Api.Management.CreateTenantAsync("documents:read");

        using var denied = await host.GetKeyAsync(writeOnly, host.Handle, host.Epoch, host.KeyId);
        using var unauthenticated = await host.GetKeyAsync(anonymous, host.Handle, host.Epoch, host.KeyId);
        using var internalListener = await host.Api.Server.Client.GetAsync(new Uri(host.Keys + query, UriKind.Relative));
        using var otherTenant = await stranger.Data.GetAsync(new Uri(host.Keys + query, UriKind.Relative));
        using var missingCollection = await host.Api.Client.GetAsync(new Uri($"{host.Api.Collections}/missing/shape/keys{query}", UriKind.Relative));
        using var openApi = JsonDocument.Parse(await anonymous.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));

        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await CodeAsync(denied)).IsEqualTo("forbidden_scope");
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(internalListener.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(otherTenant.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await CodeAsync(otherTenant)).IsEqualTo("unknown_database");
        await Assert.That(missingCollection.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await CodeAsync(missingCollection)).IsEqualTo("unknown_collection");
        foreach (var refused in new[] { denied, unauthenticated, otherTenant, missingCollection })
        {
            await Assert.That((await refused.Content.ReadAsStringAsync()).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
        }

        var get = openApi.RootElement.GetProperty("paths")
            .GetProperty("/v1/projects/{project}/databases/{database}/collections/{collection}/shape/keys").GetProperty("get");
        await Assert.That(get.GetProperty("operationId").GetString()).IsEqualTo("getShapeKey");
        await Assert.That(get.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString())).Contains("documents:read");
        var required = get.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query" && parameter.GetProperty("required").GetBoolean())
            .Select(parameter => parameter.GetProperty("name").GetString()!).ToList();
        await Assert.That(required).IsEquivalentTo(["handle", "epoch", "key_id"]);
    }

    [Test]
    public async Task A_revoked_api_key_gets_no_key_and_its_old_epoch_key_cannot_open_objects_sealed_after_rotation()
    {
        await using var host = await CryptoHost.StartAsync();
        var database = await host.Api.TenantOfAsync();
        var owner = host.Api.Server.Client;
        const string Keys = "/admin/api-keys";
        using var created = await owner.PostAsJsonAsync(new Uri(Keys, UriKind.Relative), new { name = _faker.Hacker.Noun(), scopes = ReadScopes, organizationId = host.Api.Tenant.OrganizationId, projectId = host.Api.Tenant.ProjectId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var reader = host.Api.Management.DataClient(issued.GetProperty("secret").GetString());
        using var before = await host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldKey = Base64Url.DecodeFromChars((await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
        var oldPlain = Encoding.UTF8.GetBytes(_faker.Lorem.Paragraph());
        using var oldObject = await host.Crypto.EncryptAsync(database, host.Handle, "2", oldPlain, CancellationToken.None);

        using var revoked = await owner.DeleteAsync(new Uri($"{Keys}/{issued.GetProperty("id").GetString()}", UriKind.Relative));
        using var sameEpoch = await host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId);
        host.Time.Advance(TimeSpan.FromMinutes(10));
        var newPlain = Encoding.UTF8.GetBytes(_faker.Lorem.Paragraph());
        using var newObject = await host.Crypto.EncryptAsync(database, host.Handle, "4", newPlain, CancellationToken.None);
        using var nextEpoch = await host.GetKeyAsync(reader, host.Handle, host.Epoch + 1, host.KeyId);
        var accepted = await host.ReleasedKeyAsync(host.Epoch + 1, host.KeyId);

        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(sameEpoch.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(nextEpoch.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(newObject.Epoch).IsEqualTo(host.Epoch + 1);
        // Whatever the revoked holder already read stays readable; nothing sealed after rotation opens with its key.
        await Assert.That(Client.Open(oldObject.Bytes.ToArray(), host.Handle, "2", host.Epoch, host.KeyId, oldKey)?.AsSpan().SequenceEqual(oldPlain)).IsTrue();
        await Assert.That(Client.Open(newObject.Bytes.ToArray(), host.Handle, "4", host.Epoch + 1, host.KeyId, oldKey)).IsNull();
        await Assert.That(Client.Open(newObject.Bytes.ToArray(), host.Handle, "4", host.Epoch, host.KeyId, oldKey)).IsNull();
        await Assert.That(Client.Open(newObject.Bytes.ToArray(), host.Handle, "4", host.Epoch + 1, host.KeyId, accepted)?.AsSpan().SequenceEqual(newPlain)).IsTrue();
    }

    [Test]
    public async Task A_machine_whose_catalog_is_unproven_for_an_epoch_withholds_keys_until_anti_entropy_proves_it_again()
    {
        // A partition can hold back a revocation committed on another machine: a after b is cut off from it.
        await using var pair = await Pair.StartAsync();
        var host = pair.Host;

        // Past a membership heartbeat, a lists b, which no comparison has proved yet. Tenant rounds never prove the
        // catalog: it has its own rounds, so a slow tenant repair cannot hold its proofs back either.
        host.Time.Advance(TimeSpan.FromSeconds(6));
        var antiEntropy = host.Api.Server.Services.GetRequiredService<AntiEntropy>();
        await antiEntropy.RunRoundAsync(CancellationToken.None, system: false);
        await antiEntropy.RunRoundAsync(CancellationToken.None, system: false);
        await AssertCatalogStaleAsync(await pair.GetKeyAsync());
        await pair.ProveAsync();
        await AssertReleasedAsync(host, await pair.GetKeyAsync());

        pair.Cut();
        // The proof began exactly now: it holds for the whole epoch and not a second more.
        host.Time.Advance(TimeSpan.FromSeconds(599));
        await AssertReleasedAsync(host, await pair.GetKeyAsync());
        host.Time.Advance(TimeSpan.FromSeconds(2));
        await AssertCatalogStaleAsync(await pair.GetKeyAsync());
        // Refused before the tenancy checks, so a stale machine reveals nothing about collections either.
        await AssertCatalogStaleAsync(await host.Api.Client.GetAsync(new Uri(
            $"{host.Api.Collections}/missing/shape/keys?handle={host.Handle}&epoch={Decimal(host.Crypto.CurrentEpoch)}&key_id={host.KeyId}", UriKind.Relative)));

        pair.Heal();
        // Past a membership heartbeat, a reads b's real route again.
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await pair.ProveAsync();
        await AssertReleasedAsync(host, await pair.GetKeyAsync());
    }

    [Test]
    public async Task An_empty_or_partial_directory_never_lifts_the_catalog_bound()
    {
        await using var pair = await Pair.StartAsync();
        var host = pair.Host;
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await pair.ProveAsync();
        var listed = await pair.Members.ListAsync(CancellationToken.None);

        // An empty member directory proves nothing about this machine, even with b just proved.
        foreach (var member in listed)
        {
            pair.Members.Leave(member.Name);
        }

        host.Time.Advance(TimeSpan.FromSeconds(6));
        await AssertCatalogStaleAsync(await pair.GetKeyAsync());
        foreach (var member in listed)
        {
            pair.Members.Join(member);
        }

        host.Time.Advance(TimeSpan.FromSeconds(6));
        await AssertReleasedAsync(host, await pair.GetKeyAsync());

        // A self-only view: b is no longer listed, but the catalog records its region, which still needs a proof in the window.
        pair.Members.Leave("b");
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await AssertReleasedAsync(host, await pair.GetKeyAsync());
        host.Time.Advance(TimeSpan.FromSeconds(600));
        await AssertCatalogStaleAsync(await pair.GetKeyAsync());

        pair.Members.Join(listed.Single(member => member.Name == "b"));
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await pair.ProveAsync();
        await AssertReleasedAsync(host, await pair.GetKeyAsync());
    }

    [Test]
    public async Task A_catalog_that_goes_stale_during_the_key_lookup_releases_no_key()
    {
        var lookup = new GatedLineage();
        await using var pair = await Pair.StartAsync(collection =>
        {
            lookup.Inner = (IShapeLineages)collection.Last(service => service.ServiceType == typeof(IShapeLineages)).ImplementationInstance!;
            collection.AddSingleton<IShapeLineages>(lookup);
        });
        var host = pair.Host;
        // Mid-epoch, so the lookup below ends in the epoch the request names.
        host.Time.Advance(TimeSpan.FromSeconds(300));
        await pair.ProveAsync();
        pair.Cut();
        host.Time.Advance(TimeSpan.FromSeconds(590));
        await AssertReleasedAsync(host, await pair.GetKeyAsync());

        // Fresh at the first check; the lookup then outlives the proof, within the endpoint's 30 s deadline.
        var entered = lookup.Arm();
        var pending = pair.GetKeyAsync();
        await entered.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(20));
        lookup.Release();
        await AssertCatalogStaleAsync(await pending.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken));
    }

    [Test]
    public async Task An_api_key_revoked_after_authentication_gets_no_key()
    {
        // Authentication read the key as valid; its revocation lands before the release, as a healing partition delivers it.
        var lookup = new GatedLineage();
        await using var host = await CryptoHost.StartAsync(services: collection =>
        {
            lookup.Inner = (IShapeLineages)collection.Last(service => service.ServiceType == typeof(IShapeLineages)).ImplementationInstance!;
            collection.AddSingleton<IShapeLineages>(lookup);
        });
        var owner = host.Api.Server.Client;
        const string Keys = "/admin/api-keys";
        using var created = await owner.PostAsJsonAsync(new Uri(Keys, UriKind.Relative), new { name = _faker.Hacker.Noun(), scopes = ReadScopes, organizationId = host.Api.Tenant.OrganizationId, projectId = host.Api.Tenant.ProjectId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var reader = host.Api.Management.DataClient(issued.GetProperty("secret").GetString());
        using (var before = await host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId))
        {
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var entered = lookup.Arm();
        var pending = host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId);
        await entered.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var revoked = await owner.DeleteAsync(new Uri($"{Keys}/{issued.GetProperty("id").GetString()}", UriKind.Relative));
        lookup.Release();
        using var refused = await pending.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await CodeAsync(refused)).IsEqualTo("invalid_key");
        await Assert.That(refused.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That((await refused.Content.ReadAsStringAsync()).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
        // The owner's own key, authenticated the same way, is still released.
        await Assert.That((await host.ReleasedKeyAsync(host.Epoch, host.KeyId)).Length).IsEqualTo(32);
    }

    [Test]
    public async Task A_historic_pair_is_released_only_with_a_retained_object_proof_and_an_unproven_key_is_never_read()
    {
        var retention = new ProvenPairs();
        await using var host = await CryptoHost.StartAsync(services => services.AddSingleton<IShapeKeyRetention>(retention));
        var (provenId, provenKey) = await host.SeedKeyAsync(Start.AddDays(-2), RandomNumberGenerator.GetBytes(32));
        var (unprovenId, _) = await host.SeedKeyAsync(Start.AddDays(-3), RandomNumberGenerator.GetBytes(32));
        retention.Prove(host.Handle, host.Epoch - 1, provenId);
        var unprovenReads = host.S3.ObserveKey(BackupLayout.Key(host.Scope, unprovenId));

        using var released = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch - 1, provenId);
        using var otherEpoch = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch - 2, provenId);
        using var refused = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch - 1, unprovenId);

        await Assert.That(released.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("epoch").GetString()).IsEqualTo(Decimal(host.Epoch - 1));
        await Assert.That(body.GetProperty("key_id").GetString()).IsEqualTo(provenId);
        await Assert.That(Convert.ToHexStringLower(Base64Url.DecodeFromChars(body.GetProperty("key").GetString()!)))
            .IsEqualTo(Convert.ToHexStringLower(Client.EpochKey(provenKey, host.Handle, host.Epoch - 1)));
        await AssertRefetchAsync(otherEpoch, host.Handle);
        await AssertRefetchAsync(refused, host.Handle);
        await Assert.That(unprovenReads.Gets).IsEqualTo(0);
        await Assert.That(unprovenReads.Unwraps).IsEqualTo(0);
    }

    [Test]
    [Arguments("list")]
    [Arguments("read")]
    public async Task A_failing_key_store_answers_unavailable_without_caching(string failure)
    {
        await using var host = await CryptoHost.StartAsync();
        var prefix = BackupLayout.Keys(host.Scope);
        if (failure == "list")
        {
            host.S3.BeforeList = (listed, _) => listed.StartsWith(prefix, StringComparison.Ordinal)
                ? throw new HttpRequestException("injected list failure") : Task.CompletedTask;
        }
        else
        {
            host.S3.BeforeGet = (key, _) => key.StartsWith(prefix, StringComparison.Ordinal)
                ? throw new HttpRequestException("injected read failure") : Task.CompletedTask;
        }

        using var failed = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, host.KeyId);
        host.S3.BeforeList = null;
        host.S3.BeforeGet = null;
        using var recovered = await host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, host.KeyId);

        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await CodeAsync(failed)).IsEqualTo("unavailable");
        await Assert.That(failed.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That((await failed.Content.ReadAsStringAsync()).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(recovered.Headers.CacheControl?.Private).IsTrue();
        await Assert.That(recovered.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task A_stalled_key_store_is_cancelled_at_the_deadline_and_answers_unavailable_without_caching()
    {
        await using var host = await CryptoHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = BackupLayout.Keys(host.Scope);
        host.S3.BeforeList = async (listed, token) =>
        {
            if (listed.StartsWith(prefix, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        var pending = host.GetKeyAsync(host.Api.Client, host.Handle, host.Epoch, host.KeyId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(31));
        using var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10), TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await CodeAsync(failed)).IsEqualTo("unavailable");
        await Assert.That(failed.Headers.CacheControl?.NoStore).IsTrue();
        host.S3.BeforeList = null;
    }

    [Test]
    public async Task A_development_host_without_a_project_key_store_answers_unavailable_without_caching()
    {
        var handle = Handle();
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IShapeLineages>(new FixedLineage(new ShapeLineage(Convert.ToHexStringLower(_faker.Random.Bytes(16)), handle))));
        var epoch = api.Server.Services.GetRequiredService<IShapeCrypto>().CurrentEpoch;
        using var response = await api.Client.GetAsync(new Uri(
            $"{api.Collections}/{DocumentApi.Collection}/shape/keys?handle={handle}&epoch={Decimal(epoch)}&key_id={Ulid.NewUlid()}", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await CodeAsync(response)).IsEqualTo("unavailable");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task Sealing_reserves_memory_and_refuses_what_the_budget_or_the_format_cannot_hold()
    {
        await using var host = await CryptoHost.StartAsync(configure: settings => settings["memory-budget"] = "4194304");
        var database = await host.Api.TenantOfAsync();

        var tooLarge = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            host.Crypto.EncryptAsync(database, host.Handle, "2", new byte[MaxPlainBytes + 1], CancellationToken.None));
        var refused = await Assert.ThrowsAsync<QosMemoryException>(() =>
            host.Crypto.EncryptAsync(database, host.Handle, "2", new byte[MaxPlainBytes], CancellationToken.None));
        using var small = await host.Crypto.EncryptAsync(database, host.Handle, "2", "{}"u8.ToArray(), CancellationToken.None);

        await Assert.That(tooLarge).IsNotNull();
        await Assert.That(refused).IsNotNull();
        await Assert.That(small.Bytes.Length).IsEqualTo(HeaderBytes + 4 + 2 + TagBytes);
    }

    [Test]
    public async Task Every_positive_vector_is_reproduced_by_the_format_writer_from_the_bucket_key_and_every_rejection_is_refused()
    {
        await using var host = await CryptoHost.StartAsync();
        using var vectors = await LoadVectorsAsync();
        var positives = vectors.RootElement.GetProperty("tbs1_vectors").EnumerateArray().ToList();
        await Assert.That(positives.Count).IsGreaterThan(0);

        foreach (var vector in positives)
        {
            var (handle, offset, epoch, keyId) = Identity(vector);
            var dataKey = await host.BucketKeyAsync(keyId, Hex(vector, "project_data_key_hex"));
            var epochKey = ShapeCrypto.EpochKey(dataKey, handle, epoch);
            var salt = Hex(vector, "object_salt_hex");
            var plain = Encoding.UTF8.GetBytes(vector.GetProperty("plaintext_utf8").GetString()!);

            var bytes = ShapeCrypto.Seal(plain, handle, offset, epoch, keyId, epochKey, salt);

            await Assert.That(Convert.ToHexStringLower(epochKey)).IsEqualTo(vector.GetProperty("epoch_key_hex").GetString());
            await Assert.That(Convert.ToHexStringLower(Client.EpochKey(dataKey, handle, epoch))).IsEqualTo(vector.GetProperty("epoch_key_hex").GetString());
            await AssertIntermediatesAsync(vector, bytes, epochKey, plain);
            await Assert.That(Convert.ToHexStringLower(bytes)).IsEqualTo(vector.GetProperty("tbs1_hex").GetString());
            await Assert.That(Client.Open(Hex(vector, "tbs1_hex"), handle, offset, epoch, keyId, epochKey)?.AsSpan().SequenceEqual(plain)).IsTrue();
        }

        foreach (var vector in vectors.RootElement.GetProperty("tbs1_rejection_vectors").EnumerateArray())
        {
            var (handle, offset, epoch, keyId) = Identity(vector);
            var opened = Client.Open(Hex(vector, "tbs1_hex"), handle, offset, epoch, keyId, Hex(vector, "epoch_key_hex"));
            await Assert.That(vector.GetProperty("expected_result").GetString()).IsEqualTo("reject_without_applying");
            await Assert.That(opened is null).IsTrue().Because(vector.GetProperty("id").GetString()!);
        }
    }

    [Test]
    public async Task Every_generated_vector_matches_size_digest_and_tags_and_every_mutation_is_refused()
    {
        await using var host = await CryptoHost.StartAsync();
        using var vectors = await LoadVectorsAsync();
        var generated = vectors.RootElement.GetProperty("tbs1_generated_vectors").EnumerateArray().ToList();
        await Assert.That(generated.Count).IsGreaterThan(0);

        foreach (var vector in generated)
        {
            var (handle, offset, epoch, keyId) = Identity(vector);
            var recipe = vector.GetProperty("plaintext_recipe");
            var plain = new byte[recipe.GetProperty("length").GetInt32()];
            plain.AsSpan().Fill(Convert.FromHexString(recipe.GetProperty("repeat_byte_hex").GetString()!)[0]);
            var dataKey = await host.BucketKeyAsync(keyId, Hex(vector, "project_data_key_hex"));
            var epochKey = ShapeCrypto.EpochKey(dataKey, handle, epoch);

            var bytes = ShapeCrypto.Seal(plain, handle, offset, epoch, keyId, epochKey, Hex(vector, "object_salt_hex"));

            await Assert.That(bytes.Length).IsEqualTo(vector.GetProperty("ciphertext_size").GetInt32());
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(vector.GetProperty("ciphertext_sha256").GetString());
            await AssertIntermediatesAsync(vector, bytes, epochKey, plain);
            await Assert.That(Client.Open(bytes, handle, offset, epoch, keyId, epochKey)?.AsSpan().SequenceEqual(plain)).IsTrue();
            foreach (var mutation in vector.GetProperty("mutations").EnumerateArray())
            {
                var mutated = Mutate(mutation, bytes, Hex(vector, "object_key_hex"), plain);
                await Assert.That(mutation.GetProperty("expected_result").GetString()).IsEqualTo("reject_without_applying");
                await Assert.That(Client.Open(mutated, handle, offset, epoch, keyId, epochKey) is null).IsTrue().Because(mutation.GetProperty("id").GetString()!);
            }
        }
    }

    [Test]
    public async Task Manifest_vectors_load_as_strict_json_and_match_the_ciphertext_contract()
    {
        using var vectors = await LoadVectorsAsync();
        var http = vectors.RootElement.GetProperty("http_vectors").EnumerateArray().ToList();
        var response = http.Single(vector => vector.GetProperty("id").GetString() == "presigned-manifest").GetProperty("response");
        using var wire = JsonDocument.Parse(response.GetProperty("body_utf8").GetString()!);
        await Assert.That(JsonElement.DeepEquals(wire.RootElement, response.GetProperty("body"))).IsTrue();

        var body = wire.RootElement;
        var entry = body.GetProperty("objects")[0];
        var handle = body.GetProperty("handle").GetString()!;
        var binding = handle + "\0" + entry.GetProperty("offset").GetString();
        await Assert.That(entry.GetProperty("binding").GetString()).IsEqualTo(binding);
        var ciphertext = http.Single(vector => vector.GetProperty("id").GetString() == "tigris-sealed-ciphertext");
        var plain = ciphertext.GetProperty("expected_body");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain.GetProperty("shape_id").GetString() + "\0" + binding)));
        await Assert.That(entry.GetProperty("object").GetString()).IsEqualTo("v1/shapes/" + handle + "/" + hash);
        await Assert.That(entry.GetProperty("url").GetString()).IsEqualTo(ciphertext.GetProperty("request").GetProperty("url").GetString());
        var bytes = Hex(ciphertext.GetProperty("response"), "body_hex");
        await Assert.That(entry.GetProperty("size").GetInt64()).IsEqualTo(bytes.LongLength);
        await Assert.That(entry.GetProperty("sha256").GetString()).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static async Task AssertIntermediatesAsync(JsonElement vector, byte[] bytes, byte[] epochKey, byte[] plain)
    {
        var (handle, offset, _, _) = Identity(vector);
        var salt = Hex(vector, "object_salt_hex");
        await Assert.That(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(handle + "\0" + offset))).IsEqualTo(vector.GetProperty("binding_hex").GetString());
        await Assert.That(Convert.ToHexStringLower(Client.ObjectKey(epochKey, salt, handle, offset))).IsEqualTo(vector.GetProperty("object_key_hex").GetString());
        await Assert.That(Convert.ToHexStringLower(bytes.AsSpan(0, HeaderBytes))).IsEqualTo(vector.GetProperty("header_hex").GetString());
        if (vector.TryGetProperty("ciphertext_size", out var size))
        {
            await Assert.That(bytes.Length).IsEqualTo(size.GetInt32());
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(vector.GetProperty("ciphertext_sha256").GetString());
        }

        var frames = Client.Frames(bytes);
        var expected = vector.GetProperty("frames").EnumerateArray().ToList();
        await Assert.That(frames.Count).IsEqualTo(expected.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            var (at, length) = frames[index];
            var frame = expected[index];
            var word = bytes.AsSpan(at, 4).ToArray();
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
            await Assert.That(frame.GetProperty("index").GetString()).IsEqualTo(Decimal(index));
            await Assert.That(Convert.ToHexStringLower(word)).IsEqualTo(frame.GetProperty("length_word_hex").GetString());
            await Assert.That(Convert.ToHexStringLower(nonce)).IsEqualTo(frame.GetProperty("nonce_hex").GetString());
            await Assert.That(Convert.ToHexStringLower(Client.Associated(bytes.AsSpan(0, HeaderBytes), index, word))).IsEqualTo(frame.GetProperty("aad_hex").GetString());
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(at + 4, length)))).IsEqualTo(frame.GetProperty("ciphertext_sha256").GetString());
            await Assert.That(Convert.ToHexStringLower(bytes.AsSpan(at + 4 + length, TagBytes))).IsEqualTo(frame.GetProperty("tag_hex").GetString());
        }

        await Assert.That(frames.Sum(frame => frame.Length)).IsEqualTo(plain.Length);
    }

    // The mutations of tbs1_generated_vectors, applied to the frames of a sealed object.
    private static byte[] Mutate(JsonElement mutation, byte[] bytes, byte[] objectKey, byte[] plain)
    {
        var frames = Client.Frames(bytes);
        var pieces = frames.Select(frame => bytes.AsSpan(frame.At, 4 + frame.Length + TagBytes).ToArray()).ToList();
        switch (mutation.GetProperty("operation").GetString())
        {
            case "swap_frames":
                var indices = mutation.GetProperty("indices").EnumerateArray().Select(index => index.GetInt32()).ToArray();
                (pieces[indices[0]], pieces[indices[1]]) = (pieces[indices[1]], pieces[indices[0]]);
                break;
            case "drop_frame":
                pieces.RemoveAt(mutation.GetProperty("index").GetInt32());
                break;
            case "set_last_flag_recompute_tag_and_drop_following_frames":
                var target = mutation.GetProperty("index").GetInt32();
                var (at, length) = frames[target];
                var word = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(word, (uint)length | LastFrame);
                var nonce = new byte[12];
                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)target);
                var piece = new byte[4 + length + TagBytes];
                word.CopyTo(piece, 0);
                using (var aes = new AesGcm(objectKey, TagBytes))
                {
                    aes.Encrypt(nonce, plain.AsSpan(at - HeaderBytes - target * (4 + TagBytes), length), piece.AsSpan(4, length),
                        piece.AsSpan(4 + length, TagBytes), Client.Associated(bytes.AsSpan(0, HeaderBytes), target, word));
                }

                pieces = [.. pieces.Take(target), piece];
                break;
            default:
                throw new InvalidOperationException("unknown mutation " + mutation.GetProperty("operation").GetString());
        }

        return [.. bytes.AsSpan(0, HeaderBytes), .. pieces.SelectMany(piece => piece)];
    }

    [Test]
    public async Task A_revoked_holders_epoch_key_opens_objects_sealed_until_the_epoch_ends_and_none_sealed_after()
    {
        await using var host = await CryptoHost.StartAsync();
        var database = await host.Api.TenantOfAsync();
        var owner = host.Api.Server.Client;
        const string Keys = "/admin/api-keys";
        using var created = await owner.PostAsJsonAsync(new Uri(Keys, UriKind.Relative), new { name = _faker.Hacker.Noun(), scopes = ReadScopes, organizationId = host.Api.Tenant.OrganizationId, projectId = host.Api.Tenant.ProjectId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var reader = host.Api.Management.DataClient(issued.GetProperty("secret").GetString());

        // Worst case: the holder fetches K_e in the first second of the epoch and is revoked right after.
        host.Time.Advance(TimeSpan.FromSeconds(1));
        using var fetched = await host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId);
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var held = Base64Url.DecodeFromChars((await fetched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
        using var revoked = await owner.DeleteAsync(new Uri($"{Keys}/{issued.GetProperty("id").GetString()}", UriKind.Relative));
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var revokedAt = host.Time.GetUtcNow();
        using var refused = await host.GetKeyAsync(reader, host.Handle, host.Epoch, host.KeyId);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // The last second of the epoch still seals under the held key; the next second does not.
        host.Time.Advance(TimeSpan.FromSeconds(598));
        var lastPlain = _faker.Random.Bytes(_faker.Random.Int(1, 4096));
        using var last = await host.Crypto.EncryptAsync(database, host.Handle, "7", lastPlain, CancellationToken.None);
        var window = host.Time.GetUtcNow() - revokedAt;
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var nextPlain = _faker.Random.Bytes(_faker.Random.Int(1, 4096));
        using var next = await host.Crypto.EncryptAsync(database, host.Handle, "8", nextPlain, CancellationToken.None);

        await Assert.That(last.Epoch).IsEqualTo(host.Epoch);
        await Assert.That(Client.Open(last.Bytes.ToArray(), host.Handle, "7", host.Epoch, host.KeyId, held)?.AsSpan().SequenceEqual(lastPlain)).IsTrue();
        await Assert.That(next.Epoch).IsEqualTo(host.Epoch + 1);
        await Assert.That(Client.Open(next.Bytes.ToArray(), host.Handle, "8", host.Epoch + 1, host.KeyId, held)).IsNull();
        // Measured: newly sealed data stays readable to a revoked API key holder for < one epoch (600 s) after revocation.
        await Assert.That(window).IsEqualTo(TimeSpan.FromSeconds(598));
        await Assert.That(window).IsLessThan(TimeSpan.FromSeconds(600));
    }

    [Test]
    public async Task A_sealed_object_reveals_only_its_epoch_key_id_salt_and_exact_plaintext_length_and_the_key_id_links_a_projects_shapes()
    {
        await using var host = await CryptoHost.StartAsync();
        var database = await host.Api.TenantOfAsync();
        var otherHandle = Handle();
        var objects = new List<(string Handle, int Length, byte[] Bytes)>();
        foreach (var (handle, length) in new[] { (host.Handle, 0), (host.Handle, _faker.Random.Int(1, 4096)), (otherHandle, _faker.Random.Int(1, 4096)), (otherHandle, FrameBytes) })
        {
            using var sealedObject = await host.Crypto.EncryptAsync(database, handle, "2", _faker.Random.Bytes(length), CancellationToken.None);
            objects.Add((handle, length, sealedObject.Bytes.ToArray()));
        }

        foreach (var (handle, length, bytes) in objects)
        {
            // Size is the exact plaintext length plus 70 header bytes and 20 bytes per frame: no padding.
            await Assert.That(bytes.Length).IsEqualTo(HeaderBytes + (length / FrameBytes + 1) * (4 + TagBytes) + length);
            await Assert.That((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4))).IsEqualTo(host.Epoch);
            await Assert.That(Encoding.ASCII.GetString(bytes, 12, 26)).IsEqualTo(host.KeyId);
            // Nothing names the tenant, collection or handle in clear.
            foreach (var name in new[] { host.Api.Tenant.OrganizationId, host.Api.Tenant.ProjectId, database.DatabaseId!, DocumentApi.Collection, handle })
            {
                await Assert.That(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name).AsSpan())).IsEqualTo(-1);
            }
        }

        // Beyond section 14's "sizes and object counts": the cleartext key id is the same for every shape of the project,
        // and as a ULID it carries the project key's creation time to the millisecond.
        await Assert.That(objects.Select(item => Encoding.ASCII.GetString(item.Bytes, 12, 26)).Distinct().Count()).IsEqualTo(1);
        await Assert.That(Ulid.Parse(host.KeyId).Time).IsEqualTo(Start.AddMinutes(-1));
        await Assert.That(objects.Select(item => Convert.ToHexString(item.Bytes, 38, 32)).Distinct().Count()).IsEqualTo(objects.Count);
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response)
    {
        var request = response.RequestMessage?.RequestUri?.PathAndQuery;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(request!);
        await Assert.That(await CodeAsync(response)).IsEqualTo("invalid_request").Because(request!);
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue().Because(request!);
    }

    private static async Task AssertRefetchAsync(HttpResponseMessage response, string current)
    {
        var request = response.RequestMessage?.RequestUri?.PathAndQuery;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(request!);
        await Assert.That(await CodeAsync(response)).IsEqualTo("must_refetch").Because(request!);
        await Assert.That(response.Headers.GetValues("TB-Shape-Handle").Single()).IsEqualTo(current);
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue().Because(request!);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("handle").GetString()).IsEqualTo(current);
        await Assert.That(error.TryGetProperty("key", out _)).IsFalse();
    }

    private static async Task AssertReleasedAsync(CryptoHost host, HttpResponseMessage response)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            var key = Base64Url.DecodeFromChars((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
            await Assert.That(key.AsSpan().SequenceEqual(Client.EpochKey(host.DataKey, host.Handle, host.Crypto.CurrentEpoch))).IsTrue();
        }
    }

    private static async Task AssertCatalogStaleAsync(HttpResponseMessage response)
    {
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await CodeAsync(response)).IsEqualTo("catalog_stale");
            await Assert.That(response.Headers.RetryAfter).IsNotNull();
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That((await response.Content.ReadAsStringAsync()).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length == 0)
        {
            return null;
        }

        using var body = JsonDocument.Parse(text);
        return body.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    }

    private static (string Handle, string Offset, long Epoch, string KeyId) Identity(JsonElement vector) => (
        vector.GetProperty("handle").GetString()!, vector.GetProperty("request_offset").GetString()!,
        long.Parse(vector.GetProperty("epoch").GetString()!, NumberStyles.None, CultureInfo.InvariantCulture), vector.GetProperty("key_id").GetString()!);

    private static byte[] Hex(JsonElement vector, string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Handle() => "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static string EntityIdsAt(DateTimeOffset at) => Ulid.NewUlid(at).ToString();

    private static async Task<JsonDocument> LoadVectorsAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TerraScale.slnx")))
        {
            root = root.Parent;
        }

        var path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("the repository root holds TerraScale.slnx"),
            "docs", "terrabase", "shape-vectors.json");
        await using var file = File.OpenRead(path);
        return await JsonDocument.ParseAsync(file);
    }

    /// <summary>
    /// A node backing up to the in-memory bucket, with a fixed clock at the start of an epoch, a shape lineage fixture
    /// naming a random current handle, and a frozen project data key persisted wrapped in the bucket as the current key.
    /// </summary>
    private sealed class CryptoHost(DocumentApi api, FakeS3 s3, FakeTimeProvider time, string handle) : IAsyncDisposable
    {
        public DocumentApi Api { get; } = api;

        public FakeS3 S3 { get; } = s3;

        public FakeTimeProvider Time { get; } = time;

        public string Handle { get; } = handle;

        public long Epoch { get; } = Start.ToUnixTimeSeconds() / 600;

        public string KeyId { get; private set; } = "";

        public byte[] DataKey { get; private set; } = [];

        public string Scope => $"{Api.Tenant.OrganizationId}/{Api.Tenant.ProjectId}";

        public string Keys => $"{Api.Collections}/{DocumentApi.Collection}/shape/keys";

        public IShapeCrypto Crypto => Api.Server.Services.GetRequiredService<IShapeCrypto>();

        public static async Task<CryptoHost> StartAsync(
            Action<IServiceCollection>? services = null, Action<Dictionary<string, string>>? configure = null)
        {
            var s3 = new FakeS3();
            var time = new FakeTimeProvider(Start);
            var handle = ShapeCryptoTests.Handle();
            var lineage = new ShapeLineage(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), handle);
            var api = await DocumentApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                configure?.Invoke(settings);
            }, collection =>
            {
                s3.Route(collection);
                ManualBackupHosting.Configure(collection);
                collection.AddSingleton<TimeProvider>(time);
                collection.AddSingleton<IShapeLineages>(new FixedLineage(lineage));
                services?.Invoke(collection);
            });
            var host = new CryptoHost(api, s3, time, handle);
            try
            {
                // Nothing has selected this new project's key yet, so the newest persisted key becomes current.
                (host.KeyId, host.DataKey) = await host.SeedKeyAsync(Start.AddMinutes(-1), RandomNumberGenerator.GetBytes(32));
                return host;
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public Task<(string Id, byte[] Key)> SeedKeyAsync(DateTimeOffset at, byte[] key)
        {
            var id = EntityIdsAt(at);
            S3.StoreObject(BackupLayout.Key(Scope, id), Encoding.ASCII.GetBytes(S3.Wrap(key)));
            return Task.FromResult((id, key));
        }

        /// <summary>Persists <paramref name="key"/> wrapped under <paramref name="id"/> and reads it back through the server's <see cref="DataKeys"/>.</summary>
        public async Task<byte[]> BucketKeyAsync(string id, byte[] key)
        {
            S3.StoreObject(BackupLayout.Key(Scope, id), Encoding.ASCII.GetBytes(S3.Wrap(key)));
            var read = await Api.Server.Services.GetRequiredService<DataKeys>().GetAsync(Scope, id, CancellationToken.None);
            await Assert.That(read.AsSpan().SequenceEqual(key)).IsTrue();
            return [.. read];
        }

        public Task<HttpResponseMessage> GetKeyAsync(HttpClient client, string handle, long epoch, string keyId) =>
            client.GetAsync(new Uri($"{Keys}?handle={handle}&epoch={Decimal(epoch)}&key_id={keyId}", UriKind.Relative));

        public async Task<byte[]> ReleasedKeyAsync(long epoch, string keyId)
        {
            using var response = await GetKeyAsync(Api.Client, Handle, epoch, keyId);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return Base64Url.DecodeFromChars((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!);
        }

        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    /// <summary>
    /// a (gru, the key server) and b (sjc) in one directory. a's writes answer after their local commit, so its setup needs
    /// no peer stream; its catalog proofs are what is under test. Cut drops a's route to b, as a partition does.
    /// </summary>
    private sealed class Pair(CryptoHost host, TerraBaseTestServer peer, FakeMemberStore members) : IAsyncDisposable
    {
        public CryptoHost Host { get; } = host;

        public FakeMemberStore Members { get; } = members;

        public static async Task<Pair> StartAsync(Action<IServiceCollection>? services = null)
        {
            var members = new FakeMemberStore();
            var host = await CryptoHost.StartAsync(services: collection =>
            {
                collection.AddSingleton<IMemberDirectory>(members).AddSingleton<IRemoteDurability>(new LocalDurability());
                services?.Invoke(collection);
            }, configure: settings =>
            {
                settings["node-name"] = "a";
                settings["region"] = "gru";
            });
            try
            {
                var peer = await TerraBaseTestServer.StartAsync(settings =>
                {
                    host.S3.Configure(settings);
                    settings["node-name"] = "b";
                    settings["region"] = "sjc";
                }, collection =>
                {
                    host.S3.Route(collection);
                    ManualBackupHosting.Configure(collection);
                    collection.AddSingleton<IMemberDirectory>(members);
                });
                return new Pair(host, peer, members);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public Task<HttpResponseMessage> GetKeyAsync() => Host.GetKeyAsync(Host.Api.Client, Host.Handle, Host.Crypto.CurrentEpoch, Host.KeyId);

        /// <summary>Runs a's catalog rounds until one proves b as of now (a bucket still settling waits a second round).</summary>
        public async Task ProveAsync()
        {
            var antiEntropy = Host.Api.Server.Services.GetRequiredService<AntiEntropy>();
            for (var round = 0; round < 5; round++)
            {
                await antiEntropy.RunRoundAsync(CancellationToken.None, system: true);
                if ((await antiEntropy.CatalogHeldWithinAsync(TimeSpan.FromTicks(1), CancellationToken.None)).Contains("b"))
                {
                    return;
                }
            }

            throw new TimeoutException("anti-entropy never proved b's catalog");
        }

        public void Cut() => Members.Map = (viewer, member) => viewer == "a" && member.Name == "b" ? member with { PeerAddress = "http://127.0.0.1:9/" } : member;

        public void Heal() => Members.Map = null;

        public async ValueTask DisposeAsync()
        {
            await peer.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    /// <summary>The fixture's lineage; once armed, the next handle lookup waits for <see cref="Release"/>.</summary>
    private sealed class GatedLineage : IShapeLineages
    {
        private TaskCompletionSource? _entered;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IShapeLineages Inner { get; set; } = null!;

        public Task Arm() => (_entered = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void Release() => _released.TrySetResult();

        public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) => Inner.ResolveAsync(scope, cancellationToken);

        public async ValueTask<LineageResult> ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _entered, null) is { } entered)
            {
                entered.TrySetResult();
                await _released.Task;
            }

            return await Inner.ResolveHandleAsync(database, collection, handle, cancellationToken);
        }
    }

    private sealed class FixedLineage(ShapeLineage lineage) : IShapeLineages
    {
        public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(lineage);

        ValueTask<LineageResult> IShapeLineages.ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(lineage);
    }

    /// <summary>The real budget; the first armed reservation moves the clock into the next epoch, as a slow admission would.</summary>
    private sealed class RotatingBudget(IMemoryBudget budget, FakeTimeProvider time) : IMemoryBudget
    {
        private int _outstanding;

        public bool RotateOnNextReserve { get; set; }

        /// <summary>Reservations taken through this budget and not yet released.</summary>
        public int Outstanding => Volatile.Read(ref _outstanding);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => Track(budget.TryReserve(lane, bytes));

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) => Track(budget.TryReserve(lane, bytes, tenant));

        private Counted? Track(IMemoryReservation? reservation)
        {
            if (RotateOnNextReserve)
            {
                RotateOnNextReserve = false;
                time.Advance(TimeSpan.FromMinutes(10));
            }

            if (reservation is null)
            {
                return null;
            }

            Interlocked.Increment(ref _outstanding);
            return new Counted(reservation, this);
        }

        private sealed class Counted(IMemoryReservation reservation, RotatingBudget owner) : IMemoryReservation
        {
            public long Bytes => reservation.Bytes;

            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._outstanding);
                    reservation.Dispose();
                }
            }
        }
    }

    /// <summary>Stands in for the T1 mirror (FR-22): the retained objects' (handle, epoch, key id) pairs a test proves.</summary>
    private sealed class ProvenPairs : IShapeKeyRetention
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, string), bool> _pairs = new();

        public void Prove(string handle, long epoch, string keyId) => _pairs[(handle, epoch, keyId)] = true;

        public ValueTask<bool> HasObjectAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_pairs.ContainsKey((handle, epoch, keyId)));
    }

    /// <summary>A strict TBS1 client written from section 12 alone; <see cref="Open"/> returns null for any rejection.</summary>
    private static class Client
    {
        public static byte[] EpochKey(byte[] dataKey, string handle, long epoch) => HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, new byte[32],
            [.. "TBS1 epoch"u8, 0, .. Encoding.UTF8.GetBytes(handle), 0, .. Encoding.UTF8.GetBytes(Decimal(epoch))]);

        public static byte[] ObjectKey(byte[] epochKey, byte[] salt, string handle, string offset) => HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, salt,
            [.. "TBS1 object"u8, 0, .. Encoding.UTF8.GetBytes(handle), 0, .. Encoding.UTF8.GetBytes(offset)]);

        public static byte[] Associated(ReadOnlySpan<byte> header, int index, ReadOnlySpan<byte> word)
        {
            var associated = new byte[HeaderBytes + 8 + 4];
            header.CopyTo(associated);
            BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(HeaderBytes), (ulong)index);
            word.CopyTo(associated.AsSpan(HeaderBytes + 8));
            return associated;
        }

        /// <summary>The (start, plaintext length) of each frame of a well-formed object.</summary>
        public static List<(int At, int Length)> Frames(byte[] bytes)
        {
            var frames = new List<(int, int)>();
            for (var at = HeaderBytes; at < bytes.Length;)
            {
                var length = (int)(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)) & ~LastFrame);
                frames.Add((at, length));
                at += 4 + length + TagBytes;
            }

            return frames;
        }

        public static byte[]? Open(byte[] bytes, string handle, string offset, long epoch, string keyId, byte[] epochKey)
        {
            if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 4).SequenceEqual("TBS1"u8)
                || BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4)) != (ulong)epoch
                || !bytes.AsSpan(12, 26).SequenceEqual(Encoding.ASCII.GetBytes(keyId)))
            {
                return null;
            }

            var header = bytes.AsSpan(0, HeaderBytes);
            using var aes = new AesGcm(ObjectKey(epochKey, header[38..].ToArray(), handle, offset), TagBytes);
            var plain = new List<byte>();
            var nonce = new byte[12];
            for (int at = HeaderBytes, index = 0; ; index++)
            {
                if (bytes.Length - at < 4)
                {
                    return null;
                }

                var word = bytes.AsSpan(at, 4);
                var value = BinaryPrimitives.ReadUInt32BigEndian(word);
                var length = (int)(value & ~LastFrame);
                var last = (value & LastFrame) != 0;
                if (length > FrameBytes || (!last && length != FrameBytes) || (last && length == FrameBytes)
                    || plain.Count + length > MaxPlainBytes || bytes.Length - at - 4 < length + TagBytes)
                {
                    return null;
                }

                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
                var piece = new byte[length];
                try
                {
                    aes.Decrypt(nonce, bytes.AsSpan(at + 4, length), bytes.AsSpan(at + 4 + length, TagBytes), piece, Associated(header, index, word));
                }
                catch (AuthenticationTagMismatchException)
                {
                    return null;
                }

                plain.AddRange(piece);
                at += 4 + length + TagBytes;
                if (last)
                {
                    return at == bytes.Length ? [.. plain] : null;
                }
            }
        }
    }
}
