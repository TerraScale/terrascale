using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.FileStorage;

[ClassDataSource<DashboardRootFixture>]
public sealed class FileStorageJourneyTests(DashboardRootFixture fixture)
{
    private const string Bucket = "01JA00000000000000000000B1";
    private const string FileId = "01JA00000000000000000000F1";

    [Test]
    public async Task FilesUseScopedSdkCallsAndPreserveCsrfCapabilitiesAndDependencyFailures()
    {
        TestAuth auth = null!;
        var created = false;
        var completed = false;
        var unavailable = false;
        var requests = 0;
        var bucketDeletes = 0;
        var rules = new TerraBaseFileRules("/userId", new(true, false, []), new(true, false, []));
        var bucket = new TerraBaseFileBucket(Bucket, "avatars", "gru", rules);
        var capability = new TerraBaseFileUrl(FileId, "https://fixture.r2.cloudflarestorage.com/files/test?signature=synthetic",
            DateTimeOffset.UtcNow.AddMinutes(5), new Dictionary<string, string> { ["Content-Length"] = "5" });
        using var metadata = JsonDocument.Parse("""{"userId":"synthetic-user","label":"<script>"}""");
        var stored = new TerraBaseStoredFile(FileId, "hello.txt", "text/plain", 5, Convert.ToBase64String(new byte[32]), metadata.RootElement, "ready", capability.ExpiresAt);
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            configureScenario: builder => auth = (TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!,
            producerExtension: async http =>
            {
                var path = http.Request.Path.Value!;
                if (!path.Contains("/buckets", StringComparison.Ordinal)) return null;
                Interlocked.Increment(ref requests);
                var bearer = http.Request.Headers.Authorization.ToString();
                var grant = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? auth.ResolveProjectToken(bearer[7..]) : null;
                var project = path.Split('/')[3];
                if (grant is null || grant.Value.ProjectId != project) return Results.StatusCode(403);
                var scope = http.Request.Method == "GET" || path.EndsWith("/download", StringComparison.Ordinal) ? "documents:read" : "documents:write";
                if (!grant.Value.Scopes.Contains(scope)) return Results.StatusCode(403);
                if (unavailable) return Results.Json(new { error = new { code = "unavailable", message = capability.Url } }, statusCode: 503);
                if (http.Request.Method == "GET" && path.EndsWith("/buckets", StringComparison.Ordinal))
                    return Results.Json(new { items = created ? new[] { bucket } : [], next = (string?)null });
                if (http.Request.Method == "POST" && path.EndsWith("/buckets", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    await Assert.That(body.RootElement.GetProperty("rules").GetProperty("owner").GetString()).IsEqualTo("/userId");
                    created = true;
                    return Results.Json(bucket, statusCode: 201);
                }
                if (http.Request.Method == "GET" && path.EndsWith("/files", StringComparison.Ordinal))
                    return Results.Json(new { items = completed ? new[] { stored } : [], next = (string?)null });
                if (path.EndsWith("/upload", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    await Assert.That(body.RootElement.GetProperty("size").GetInt64()).IsEqualTo(5);
                    await Assert.That(body.RootElement.GetProperty("metadata").GetProperty("userId").GetString()).IsEqualTo("synthetic-user");
                    return Results.Json(capability, statusCode: 201);
                }
                if (path.EndsWith("/complete", StringComparison.Ordinal)) { completed = true; return Results.Json(stored); }
                if (path.EndsWith("/download", StringComparison.Ordinal)) return Results.Json(capability);
                if (http.Request.Method == "DELETE")
                {
                    if (path.EndsWith("/buckets/" + Bucket, StringComparison.Ordinal)) Interlocked.Increment(ref bucketDeletes);
                    completed = false;
                    return Results.NoContent();
                }
                return Results.NotFound();
            });
        using var client = scenario.CreateClient();
        using var anonymous = await client.GetAsync("/storage");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);
        var empty = await client.GetStringAsync("/storage");
        await Assert.That(empty).Contains("No buckets yet");
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["name"] = "avatars", ["owner"] = "/userId", ["readOwner"] = "1", ["writeOwner"] = "1" };
        using var create = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/bucket", form);
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var page = await client.GetAsync("/storage");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains("No completed files").And.Contains("avatars");
        await Assert.That(page.Headers.GetValues("Content-Security-Policy").Single()).Contains("https://*.r2.cloudflarestorage.com");
        form = new() { ["_csrf"] = "invalid", ["bucket"] = Bucket, ["name"] = "hello.txt", ["contentType"] = "text/plain", ["size"] = "5", ["sha256"] = stored.Sha256, ["metadata"] = metadata.RootElement.GetRawText() };
        var before = requests;
        using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/upload", form);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(requests).IsEqualTo(before);
        form["_csrf"] = csrf;
        using var upload = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/upload", form);
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(upload.Headers.CacheControl!.NoStore).IsTrue();
        using var signed = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        await Assert.That(signed.RootElement.GetProperty("id").GetString()).IsEqualTo(FileId);
        form = new() { ["_csrf"] = csrf, ["bucket"] = Bucket, ["fileId"] = FileId };
        using var complete = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/complete", form);
        await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var populated = await client.GetStringAsync("/storage");
        await Assert.That(populated).Contains("hello.txt").And.Contains("&lt;script&gt;");
        await Assert.That(populated.Contains("<script>", StringComparison.Ordinal)).IsFalse();
        await Assert.That(populated.Contains("signature=synthetic", StringComparison.Ordinal)).IsFalse();
        using var download = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/download", form);
        await Assert.That(download.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(download.Headers.Location!.AbsoluteUri).IsEqualTo(capability.Url);
        await Assert.That(download.Headers.CacheControl!.NoStore).IsTrue();
        using var delete = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/delete", form);
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await client.GetStringAsync("/storage")).Contains("No completed files");
        foreach (var confirmation in new[] { "", "other-bucket", "Avatars" })
        {
            using var wrongBucket = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/delete-bucket", new()
            { ["_csrf"] = csrf, ["bucket"] = Bucket, ["confirmName"] = confirmation });
            await Assert.That(wrongBucket.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(bucketDeletes).IsEqualTo(0);
        }
        using var deleteBucket = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/storage/delete-bucket", new()
        { ["_csrf"] = csrf, ["bucket"] = Bucket, ["confirmName"] = "avatars" });
        await Assert.That(deleteBucket.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(bucketDeletes).IsEqualTo(1);
        unavailable = true;
        using var error = await client.GetAsync("/storage");
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var failure = await error.Content.ReadAsStringAsync();
        await Assert.That(failure).Contains("File storage is temporarily unavailable");
        await Assert.That(failure.Contains("signature=synthetic", StringComparison.Ordinal)).IsFalse();
    }
}
