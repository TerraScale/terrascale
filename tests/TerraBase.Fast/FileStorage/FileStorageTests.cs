using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Features.FileStorage;
using TerraBase.Testing;

namespace TerraBase.Fast.FileStorage;

public sealed class FileStorageTests
{
    [Test]
    public async Task Owner_upload_is_hidden_until_verified_and_second_user_gets_403()
    {
        var objects = new Objects();
        await using var api = await ProjectApi.StartAsync(services => services.Replace(ServiceDescriptor.Singleton<IFileObjectStore>(objects)), CancellationToken.None);
        var buckets = $"/v1/projects/{api.Database.Project}/buckets";
        var rules = new CollectionAccessRules("/owner", new(true, false, []), new(true, false, []));
        var created = await Send(api.Client, HttpMethod.Post, buckets, new { name = "avatars", rules });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var bucket = buckets + "/" + created.Json.GetProperty("id").GetString();
        using var alice = User(api, DocumentApi.NewId());
        using var bob = User(api, DocumentApi.NewId());
        var actor = alice.DefaultRequestHeaders.GetValues("X-Test-Actor").Single();
        var upload = await Send(alice, HttpMethod.Post, bucket + "/files/upload", new { name = "hello.txt", contentType = "text/plain", size = 5, sha256 = Convert.ToBase64String(new byte[32]), metadata = new { owner = actor } });
        await Assert.That(upload.Status).IsEqualTo(HttpStatusCode.Created);
        var file = bucket + "/files/" + upload.Json.GetProperty("id").GetString();
        await Assert.That((await Send(alice, HttpMethod.Post, file + "/download")).Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await Send(bob, HttpMethod.Post, file + "/complete")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        objects.Valid = false;
        await Assert.That((await Send(alice, HttpMethod.Post, file + "/complete")).Status).IsEqualTo(HttpStatusCode.Conflict);
        objects.Valid = true;
        await Assert.That((await Send(alice, HttpMethod.Post, file + "/complete")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await Send(alice, HttpMethod.Post, file + "/download")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await Send(bob, HttpMethod.Post, file + "/download")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await Send(bob, HttpMethod.Get, bucket + "/files")).Json.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await Send(bob, HttpMethod.Delete, file)).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await Send(alice, HttpMethod.Put, bucket + "/rules", new { rules })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await Send(api.Client, HttpMethod.Delete, bucket)).Status).IsEqualTo(HttpStatusCode.Conflict);
        using var narrowed = api.ProjectClient(database: api.Database.Id);
        await Assert.That((await Send(narrowed, HttpMethod.Get, buckets)).Status).IsEqualTo(HttpStatusCode.Forbidden);
        var used = await api.Api.Server.Services.GetRequiredService<IFileStorageUsage>().BytesAsync(api.Database.Organization, api.Database.Project, CancellationToken.None);
        await Assert.That(used).IsEqualTo(5);
        await Assert.That((await Send(alice, HttpMethod.Delete, file)).Status).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await Send(alice, HttpMethod.Post, file + "/download")).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task File_signing_uses_only_its_independent_identity(bool backupConfigured, bool completeIdentity)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var backups = new FakeS3();
        await using var api = await DocumentApi.StartAsync(settings =>
        {
            if (backupConfigured)
            {
                backups.Configure(settings);
            }
            settings["files-s3-endpoint"] = "https://file-account.r2.cloudflarestorage.com";
            settings["files-s3-access-key-id"] = "file-only-test-key";
            if (completeIdentity)
            {
                settings["files-s3-secret-access-key"] = "file-only-test-secret";
            }
        }, backupConfigured ? backups.Route : null, cancellationToken: token);
        await Assert.That((await api.SendAsync(HttpMethod.Get, api.Documents, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.OK);
        using var owner = api.Management.As(api.Tenant.User);
        using var project = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, descriptor =>
        {
            descriptor.Claims["project_id"] = api.Tenant.ProjectId;
            descriptor.Claims["scope"] = "documents:read documents:write";
        }));
        var buckets = api.Project + "/buckets";
        var created = await Send(project, HttpMethod.Post, buckets,
            new { name = "files", rules = new CollectionAccessRules(null, new(false, true, []), new(false, true, [])) });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var upload = await Send(project, HttpMethod.Post, buckets + "/" + created.Json.GetProperty("id").GetString() + "/files/upload",
            new { name = "one", contentType = "text/plain", size = 1, sha256 = Convert.ToBase64String(new byte[32]), metadata = new { } });
        await Assert.That(upload.Status).IsEqualTo(completeIdentity ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable);
        if (completeIdentity)
        {
            var signed = new Uri(upload.Json.GetProperty("url").GetString()!);
            await Assert.That(signed.Host).IsEqualTo("file-account.r2.cloudflarestorage.com");
            await Assert.That(signed.AbsolutePath.StartsWith("/terrascale-test-region-files/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(signed.Query.Contains("file-only-test-key", StringComparison.Ordinal)).IsTrue();
        }
        var used = await api.Server.Services.GetRequiredService<IFileStorageUsage>().BytesAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, token);
        await Assert.That(used).IsEqualTo(completeIdentity ? 1 : 0);
    }

    private static HttpClient User(ProjectApi api, string actor)
    {
        var client = api.Api.WithToken(api.Api.Issuer.Mint(actor, token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = api.Database.Project;
            token.Claims["instance_id"] = api.Database.Project;
            token.Claims["roles"] = Array.Empty<string>();
        }));
        client.DefaultRequestHeaders.Add("X-Test-Actor", actor);
        return client;
    }
    private static Task<ApiResponse> Send(HttpClient client, HttpMethod method, string path, object? body = null) =>
        DocumentApi.SendAsync(client, method, path, body is null ? null : JsonContent.Create(body));

    private sealed class Objects : IFileObjectStore
    {
        public bool Valid { get; set; } = true;
        public ValueTask<FileSignedUrl> UploadAsync(string region, string key, FileObject file, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileSignedUrl(file.Id, "https://files.example/upload", file.UploadExpiresAt, []));
        public ValueTask<FileSignedUrl> DownloadAsync(string region, string key, FileObject file, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileSignedUrl(file.Id, "https://files.example/download", DateTimeOffset.UtcNow.AddMinutes(1), []));
        public ValueTask<bool> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken) => ValueTask.FromResult(Valid);
        public ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
