using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Catalog;

public sealed class LocalCatalogAnchorTests
{
    private const string Anchor = "catalog-anchor/local";
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task A_current_catalog_reopens_from_its_local_anchor_and_serves_its_persisted_records(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-local-anchor-").FullName;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider();
        var user = EntityIds.New().ToString();
        try
        {
            string organization;
            await using (var api = await StartAsync(directory, s3, time, user))
            {
                organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
                await Assert.That(await api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(cancellationToken)).IsTrue();
                await Assert.That(s3.Objects.ContainsKey(Anchor)).IsTrue();
                using var ready = await api.Server.Client.GetAsync(ManagementApi.Uri("/readyz"), cancellationToken);
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await using var reopened = await StartAsync(directory, s3, time, user);
            using var records = await reopened.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations"), cancellationToken);
            await Assert.That(records.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await records.Content.ReadAsStringAsync(cancellationToken)).Contains(organization, StringComparison.Ordinal)).IsTrue();
            using var restartedReady = await reopened.Server.Client.GetAsync(ManagementApi.Uri("/readyz"), cancellationToken);
            await Assert.That(restartedReady.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(s3.InconsistentAnchorReads).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("newer-offset")]
    [Arguments("different-incarnation")]
    [Arguments("missing")]
    [Arguments("tampered")]
    public async Task A_restored_catalog_cannot_resurrect_a_revoked_credential_or_release_shape_keys(string evidence, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-local-rollback-").FullName;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider();
        var snapshot = Path.Combine(directory, ".before-revocation.db");
        try
        {
            string secret;
            string id;
            string shape;
            byte[]? replacement = null;
            await using (var api = await StartAsync(directory, s3, time, EntityIds.New().ToString()))
            {
                var database = await api.CreateManagedDatabaseAsync();
                shape = $"/v1/projects/{database.Project}/databases/{database.Name}/collections/people/shape";
                using var created = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
                    new { name = new Faker().Hacker.Noun(), scopes = ReadScopes, organizationId = database.Organization, projectId = database.Project }, cancellationToken);
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var key = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                secret = key.GetProperty("secret").GetString()!;
                id = key.GetProperty("id").GetString()!;
                await using (var tap = await api.Server.Services.GetRequiredService<IWalBackup>().OpenTapAsync(null, cancellationToken))
                {
                    using var log = new MemoryStream();
                    await tap!.SnapshotAsync(log, snapshot, cancellationToken);
                }

                using var revoked = await api.Server.Client.DeleteAsync(ManagementApi.Uri($"/admin/api-keys/{id}"), cancellationToken);
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                if (evidence == "missing")
                {
                    await Assert.That(await api.Server.Services.GetRequiredService<IObjectStore>().DeleteAsync(Anchor, cancellationToken)).IsTrue();
                    s3.FailPut = IsAnchor;
                }
                else if (evidence == "different-incarnation")
                {
                    replacement = await DifferentIncarnationAsync(api.Server, cancellationToken);
                }
            }

            if (replacement is not null)
            {
                s3.StoreObject(Anchor, replacement);
            }
            else if (evidence == "tampered")
            {
                s3.Objects[Anchor][^1] ^= 0xff;
            }

            File.Move(snapshot, Path.Combine(directory, "_system.db"), overwrite: true);
            File.Move(snapshot + ".key", Path.Combine(directory, "_system.db.key"), overwrite: true);
            File.Delete(Path.Combine(directory, "_system.db-wal"));
            File.Delete(Path.Combine(directory, "_system.db-shm"));
            await using var restored = await StartAsync(directory, s3, time, EntityIds.New().ToString());
            using var data = restored.DataClient(secret);
            using var keyRequest = await data.GetAsync(ManagementApi.Uri(
                $"{shape}/keys?handle=h1-0000000000000000&epoch=0&key_id={EntityIds.New()}"), cancellationToken);
            await Assert.That(keyRequest.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await CodeAsync(keyRequest, cancellationToken)).IsEqualTo("catalog_unverified");
            await Assert.That((await keyRequest.Content.ReadAsStringAsync(cancellationToken)).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
            using var revocation = await restored.Server.Client.DeleteAsync(ManagementApi.Uri($"/admin/api-keys/{id}"), cancellationToken);
            await Assert.That(revocation.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await CodeAsync(revocation, cancellationToken)).IsEqualTo("catalog_unverified");
            using var ready = await restored.Server.Client.GetAsync(ManagementApi.Uri("/readyz"), cancellationToken);
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(restored.Server.Services.GetRequiredService<ICatalogGate>().IsOpen).IsFalse();
            await Assert.That(s3.InconsistentAnchorReads).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_failed_restore_anchor_never_acknowledges_revocation_and_the_key_stops_authenticating(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        using var created = await host.Api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"), new
        {
            name = new Faker().Hacker.Noun(),
            scopes = ReadScopes,
            organizationId = host.Database.Organization,
            projectId = host.Database.Project,
        }, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        using var reader = host.Api.DataClient(key.GetProperty("secret").GetString());
        var shape = $"/v1/projects/{host.Database.Project}/databases/{host.Database.Name}/collections/people/shape";
        using var admitted = await reader.GetAsync(ManagementApi.Uri(shape + "?offset=-1"), cancellationToken);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var handle = admitted.Headers.GetValues("TB-Shape-Handle").Single();
        host.S3.FailPut = IsAnchor;
        using var revoked = await host.Api.Server.Client.DeleteAsync(
            ManagementApi.Uri($"/admin/api-keys/{key.GetProperty("id").GetString()}"), cancellationToken);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await CodeAsync(revoked, cancellationToken)).IsEqualTo("catalog_anchor_unavailable");
        using var denied = await reader.GetAsync(ManagementApi.Uri(
            $"{shape}/keys?handle={handle}&epoch=0&key_id={EntityIds.New()}"), cancellationToken);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await denied.Content.ReadAsStringAsync(cancellationToken)).Contains("\"key\"", StringComparison.Ordinal)).IsFalse();
        host.S3.FailPut = _ => false;
        await Assert.That(await host.Api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(cancellationToken)).IsTrue();
    }

    private static Task<ManagementApi> StartAsync(string directory, FakeS3 s3, FakeTimeProvider time, string user) =>
        ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["data-directory"] = directory;
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time, userId: user);

    private static bool IsAnchor(string key) => key.StartsWith("catalog-anchor/", StringComparison.Ordinal);

    private static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("error").GetProperty("code").GetString();

    private static async Task<byte[]> DifferentIncarnationAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        var (keyId, key) = await server.Services.GetRequiredService<DataKeys>().CurrentAsync(BackupLayout.KeyScope(null), cancellationToken);
        using var plain = new MemoryStream();
        await using (var json = new Utf8JsonWriter(plain))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 2);
            json.WriteString("incarnation", EntityIds.New().ToString());
            json.WriteNumber("offset", 0);
            json.WriteEndObject();
        }

        plain.Position = 0;
        using var sealedAnchor = new MemoryStream();
        await BackupObject.WriteAsync(plain, sealedAnchor, Anchor, keyId, key, cancellationToken);
        return sealedAnchor.ToArray();
    }
}
