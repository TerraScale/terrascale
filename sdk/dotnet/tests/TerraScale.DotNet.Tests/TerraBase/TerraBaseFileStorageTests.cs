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

}
