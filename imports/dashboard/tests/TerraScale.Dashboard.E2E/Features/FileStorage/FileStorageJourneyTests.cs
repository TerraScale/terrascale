using System.Net;
using System.Net.Http.Json;
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
        var enrolled = false;
        var completed = false;
        var unavailable = false;
        var requests = 0;
        var bucketDeletes = 0;
        var paginate = false;
        var rulesUpdated = false;
        var bucketAfter = "";
        var fileAfter = "";
        string? invalidDownload = null;
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
                {
                    // Like TerraBase, a project with no database or bucket yet is not enrolled and has nothing to list.
                    if (!enrolled) return Results.Json(new { error = new { code = "unknown_database", message = "the database does not exist" } }, statusCode: 404);
                    bucketAfter = http.Request.Query["after"].ToString();
                    return Results.Json(new { items = created && bucketAfter.Length == 0 ? new[] { bucket } : [], next = paginate && bucketAfter.Length == 0 ? Bucket : null });
                }
                if (http.Request.Method == "POST" && path.EndsWith("/buckets", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    await Assert.That(body.RootElement.GetProperty("name").GetString()).IsEqualTo("avatars");
                    await Assert.That(body.RootElement.GetProperty("rules").GetProperty("owner").GetString()).IsEqualTo("/userId");
                    created = enrolled = true;
                    return Results.Json(bucket, statusCode: 201);
                }
                if (http.Request.Method == "PUT" && path.EndsWith("/rules", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    await Assert.That(body.RootElement.GetProperty("rules").GetProperty("read").GetProperty("public").GetBoolean()).IsTrue();
                    rulesUpdated = true;
                    return Results.Json(bucket);
                }
                if (http.Request.Method == "GET" && path.EndsWith("/files", StringComparison.Ordinal))
                {
                    fileAfter = http.Request.Query["after"].ToString();
                    return Results.Json(new { items = completed && fileAfter.Length == 0 ? new[] { stored } : [], next = paginate && fileAfter.Length == 0 ? FileId : null });
                }
                if (path.EndsWith("/upload", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    await Assert.That(body.RootElement.GetProperty("name").GetString()).IsEqualTo("hello.txt");
                    await Assert.That(body.RootElement.GetProperty("contentType").GetString()).IsEqualTo("text/plain");
                    await Assert.That(body.RootElement.GetProperty("sha256").GetString()).IsEqualTo(stored.Sha256);
                    await Assert.That(body.RootElement.GetProperty("size").GetInt64()).IsEqualTo(5);
                    await Assert.That(body.RootElement.GetProperty("metadata").GetProperty("userId").GetString()).IsEqualTo("synthetic-user");
                    return Results.Json(capability, statusCode: 201);
                }
                if (path.EndsWith("/complete", StringComparison.Ordinal)) { completed = true; return Results.Json(stored); }
                if (path.EndsWith("/download", StringComparison.Ordinal)) return Results.Json(capability with { Url = invalidDownload ?? capability.Url });
                if (http.Request.Method == "DELETE" && path.EndsWith("/buckets/" + Bucket, StringComparison.Ordinal))
                { Interlocked.Increment(ref bucketDeletes); created = false; return Results.NoContent(); }
                if (http.Request.Method == "DELETE") { completed = false; return Results.NoContent(); }
                return Results.NotFound();
            });
        using var client = scenario.CreateClient();
        using var anonymous = await client.GetAsync("/storage");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(client, "File storage MCP");
        var csrf = await HttpJourney.CsrfAsync(client);
        var empty = await client.GetStringAsync("/storage");
        await Assert.That(empty).Contains("No buckets yet");
        var emptyMcp = await CallAsync("list_file_storage", new { });
        await Assert.That(emptyMcp.GetProperty("buckets").GetProperty("items").GetArrayLength()).IsEqualTo(0);
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
        var uploadMetadata = new TerraBaseFileUpload("hello.txt", "text/plain", 5, stored.Sha256, metadata.RootElement);
        var publicRead = rules with { Read = new(true, true, []) };
        var operations = new Dictionary<string, Dictionary<string, object>>
        {
            ["list_file_storage"] = new() { ["bucketId"] = Bucket },
            ["create_file_bucket"] = new() { ["name"] = " avatars ", ["rules"] = rules },
            ["set_file_bucket_rules"] = new() { ["bucketId"] = Bucket, ["rules"] = publicRead },
            ["delete_file_bucket"] = new() { ["bucketId"] = Bucket, ["confirmName"] = "avatars" },
            ["create_file_upload"] = new() { ["bucketId"] = Bucket, ["upload"] = uploadMetadata },
            ["complete_file_upload"] = new() { ["bucketId"] = Bucket, ["fileId"] = FileId },
            ["delete_file"] = new() { ["bucketId"] = Bucket, ["fileId"] = FileId, ["confirmFileId"] = FileId },
            ["create_file_download"] = new() { ["bucketId"] = Bucket, ["fileId"] = FileId }
        };
        var inventory = (await HttpJourney.McpAsync(client, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools")
            .EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        before = requests;
        foreach (var (name, arguments) in operations)
        {
            await Assert.That(inventory[name].TryGetProperty("outputSchema", out _)).IsTrue();
            await ErrorAsync(name, new Dictionary<string, object>(arguments) { ["projectId"] = "01JA0000000000000000000001" });
        }
        await Assert.That(requests).IsEqualTo(before).Because("foreign projects never reach the file API");
        foreach (var name in new[] { "delete_file", "delete_file_bucket" })
            await Assert.That(inventory[name].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()).IsTrue();
        created = false;
        await CallAsync("create_file_bucket", operations["create_file_bucket"]);
        await Assert.That(created).IsTrue();
        await CallAsync("set_file_bucket_rules", operations["set_file_bucket_rules"]);
        await Assert.That(rulesUpdated).IsTrue();
        var mcpUpload = await CallAsync("create_file_upload", operations["create_file_upload"]);
        await Assert.That(mcpUpload.GetProperty("url").GetString()).IsEqualTo(capability.Url);
        await Assert.That(mcpUpload.GetProperty("headers").GetProperty("Content-Length").GetString()).IsEqualTo("5");
        await CallAsync("complete_file_upload", operations["complete_file_upload"]);
        await Assert.That(completed).IsTrue();
        paginate = true;
        var first = await CallAsync("list_file_storage", operations["list_file_storage"]);
        await Assert.That(first.GetProperty("files").GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("hello.txt");
        await Assert.That(first.GetProperty("buckets").GetProperty("next").GetString()).IsEqualTo(Bucket);
        await Assert.That(first.GetProperty("files").GetProperty("next").GetString()).IsEqualTo(FileId);
        await CallAsync("list_file_storage", new { bucketId = Bucket, bucketAfter = Bucket, after = FileId });
        await Assert.That(bucketAfter).IsEqualTo(Bucket);
        await Assert.That(fileAfter).IsEqualTo(FileId);
        paginate = false;
        var mcpDownload = await CallAsync("create_file_download", operations["create_file_download"]);
        await Assert.That(mcpDownload.GetProperty("url").GetString()).IsEqualTo(capability.Url);
        foreach (var url in new[] { "http://fixture.r2.cloudflarestorage.com/file", "https://user@fixture.r2.cloudflarestorage.com/file", "https://fixture.r2.cloudflarestorage.com/file#fragment", "https://example.test/file" })
        {
            invalidDownload = url;
            await ErrorAsync("create_file_download", operations["create_file_download"]);
        }
        invalidDownload = null;
        before = requests;
        await ErrorAsync("delete_file", new { bucketId = Bucket, fileId = FileId, confirmFileId = "wrong" });
        await Assert.That(requests).IsEqualTo(before);
        var deletesBefore = bucketDeletes;
        await ErrorAsync("delete_file_bucket", new { bucketId = Bucket, confirmName = "wrong" });
        await Assert.That(bucketDeletes).IsEqualTo(deletesBefore);
        before = requests;
        await ErrorAsync("create_file_upload", new { bucketId = Bucket, upload = uploadMetadata with { Size = 0 } });
        await ErrorAsync("create_file_upload", new { bucketId = Bucket, upload = uploadMetadata with { Metadata = JsonSerializer.SerializeToElement(Array.Empty<string>()) } });
        await Assert.That(requests).IsEqualTo(before);
        await RoleAsync("viewer");
        before = requests;
        foreach (var (name, arguments) in operations.Where(item => item.Key is not ("list_file_storage" or "create_file_download")))
            await ErrorAsync(name, arguments);
        await Assert.That(requests).IsEqualTo(before).Because("viewer writes never reach the file API");
        await CallAsync("list_file_storage", operations["list_file_storage"]);
        await CallAsync("create_file_download", operations["create_file_download"]);
        await RoleAsync("owner");
        await CallAsync("delete_file", operations["delete_file"]);
        await Assert.That(completed).IsFalse();
        await CallAsync("delete_file_bucket", operations["delete_file_bucket"]);
        await Assert.That(created).IsFalse();
        unavailable = true;
        using var error = await client.GetAsync("/storage");
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var failure = await error.Content.ReadAsStringAsync();
        await Assert.That(failure).Contains("File storage is temporarily unavailable");
        await Assert.That(failure.Contains("signature=synthetic", StringComparison.Ordinal)).IsFalse();

        async Task<JsonElement> CallAsync(string name, object arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name);
            return result.GetProperty("structuredContent");
        }
        async Task ErrorAsync(string name, object arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue().Because(name);
            await Assert.That(response.GetRawText()).DoesNotContain("signature=synthetic");
        }
        async Task RoleAsync(string value)
        {
            using var controls = scenario.CreateProviderClient();
            using var response = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value });
            response.EnsureSuccessStatusCode();
        }
    }
}
