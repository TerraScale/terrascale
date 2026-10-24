using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFileStorageTests
{
    private const string Bucket = "01J00000000000000000000001";
    private const string File = "01J00000000000000000000002";

    [Test]
    public async Task Every_file_operation_matches_the_pinned_producer_route_and_scope()
    {
        using var pin = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("file-storage/pin.json"));
        foreach (var input in pin.RootElement.GetProperty("inputs").EnumerateObject())
        {
            var bytes = TerraBaseIntegrationFixture.ReadContractBytes("file-storage/" + input.Name);
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(input.Value.GetProperty("sha256").GetString());
        }
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("file-storage/routes.json"));
        await Assert.That(manifest.RootElement.GetProperty("producerRevision").GetString()).IsEqualTo(pin.RootElement.GetProperty("revision").GetString());
        var routes = manifest.RootElement.GetProperty("routes").EnumerateArray().ToDictionary(r => r.GetProperty("operation").GetString()!, r => r);
        (string Method, string Path)? sent = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = (request.Method.Method, request.RequestUri!.AbsolutePath);
            throw new HttpRequestException("recorded");
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        using var metadata = JsonDocument.Parse("""{"owner":"alice"}""");
        var rules = new TerraBaseFileRules("/owner", new(true, false, []), new(true, false, []));
        (string Operation, Func<Task> Call)[] calls =
        [
            ("createFileBucket", () => project.CreateFileBucketAsync("avatars", rules)),
            ("listFileBuckets", () => project.ListFileBucketsAsync()),
            ("setFileBucketRules", () => project.SetFileBucketRulesAsync(Bucket, rules)),
            ("deleteFileBucket", () => project.DeleteFileBucketAsync(Bucket)),
            ("createFileUpload", () => project.CreateFileUploadAsync(Bucket, new("hello", "text/plain", 5, Convert.ToBase64String(new byte[32]), metadata.RootElement))),
            ("completeFileUpload", () => project.CompleteFileUploadAsync(Bucket, File)),
            ("listBucketFiles", () => project.ListBucketFilesAsync(Bucket)),
            ("createFileDownload", () => project.CreateFileDownloadAsync(Bucket, File)),
            ("deleteBucketFile", () => project.DeleteBucketFileAsync(Bucket, File)),
        ];
        await Assert.That(routes.Count).IsEqualTo(calls.Length);
        foreach (var (operation, call) in calls)
        {
            sent = null;
            await Assert.ThrowsAsync<HttpRequestException>(call);
            if (sent is not { } wire) throw new InvalidOperationException(operation + " sent no request");
            var route = routes[operation];
            await Assert.That(wire.Method).IsEqualTo(route.GetProperty("method").GetString());
            var path = route.GetProperty("path").GetString()!.Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal)
                .Replace("{bucket}", Bucket, StringComparison.Ordinal).Replace("{file}", File, StringComparison.Ordinal);
            await Assert.That(wire.Path).IsEqualTo(path);
            var source = TerraBaseIntegrationFixture.ReadContract("file-storage/" + route.GetProperty("source").GetString());
            await Assert.That(source.Contains("Name(\"" + operation + "\")", StringComparison.Ordinal)).IsTrue();
            await Assert.That(source.Contains("RequireAuthorization(\"" + route.GetProperty("scope").GetString() + "\")", StringComparison.Ordinal)).IsTrue();
            var success = route.GetProperty("successStatus").GetInt32();
            await Assert.That(source.Contains(success == 204 ? "Produces(StatusCodes.Status204NoContent)" : ">(" + success + ")", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task File_calls_use_project_paths_and_never_replay_denied_capabilities()
    {
        var calls = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            await Assert.That(path.StartsWith("/v1/projects/" + TerraBaseIntegrationFixture.ProjectId + "/buckets", StringComparison.Ordinal)).IsTrue();
            if (path.EndsWith("/download", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"access_denied","message":"denied"}}""") };
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/upload", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("size").GetInt64()).IsEqualTo(5);
                return new(HttpStatusCode.Created) { Content = new StringContent($$$"""{"id":"{{{File}}}","url":"https://objects.example/put","expiresAt":"2026-10-24T12:00:00Z","headers":{"Content-Length":"5"}}""") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent($$"""{"items":[],"next":"{{File}}"}""") };
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        using var metadata = JsonDocument.Parse("""{"owner":"alice"}""");
        var upload = await project.CreateFileUploadAsync(Bucket, new("hello", "text/plain", 5, Convert.ToBase64String(new byte[32]), metadata.RootElement));
        await Assert.That(upload.Id).IsEqualTo(File);
        await Assert.That(upload.ToString().Contains("objects.example", StringComparison.Ordinal)).IsFalse();
        var page = await project.ListBucketFilesAsync(Bucket, File);
        await Assert.That(page.Next).IsEqualTo(File);
        await Assert.That(async () => await project.CreateFileDownloadAsync(Bucket, File)).Throws<TerraBaseException>();
        await project.DeleteBucketFileAsync(Bucket, File);
        await Assert.That(calls.Count).IsEqualTo(4);
    }
}
