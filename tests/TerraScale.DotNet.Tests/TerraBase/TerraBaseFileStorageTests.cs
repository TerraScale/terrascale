using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFileStorageTests
{
    private const string Bucket = "01J00000000000000000000001";
    private const string File = "01J00000000000000000000002";

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
                return new(HttpStatusCode.Created) { Content = new StringContent($$"""{"id":"{{File}}","url":"https://objects.example/put","expiresAt":"2026-10-24T12:00:00Z","headers":{"Content-Length":"5"}}""") };
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
