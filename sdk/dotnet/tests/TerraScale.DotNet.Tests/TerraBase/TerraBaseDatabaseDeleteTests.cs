using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>DeleteAsync over a fake handler.</summary>
public sealed class TerraBaseDatabaseDeleteTests
{
    private static readonly TerraBaseDatabaseClientOptions Options = new()
    {
        BaseAddress = new Uri("https://terrabase.example.test/"),
        ApiKey = "tb_test_key",
    };

    [Test]
    public async Task DeleteSendsOneBearerDeleteAndSucceedsOnNoContent()
    {
        var sent = new List<(HttpMethod Method, string Uri, string? Authorization, bool HasBody)>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is not null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDatabaseClient(http, Options).Database("project", "app");

        await database.DeleteAsync();

        var request = sent.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.Uri).IsEqualTo("https://terrabase.example.test/v1/projects/project/databases/app");
        await Assert.That(request.Authorization).IsEqualTo("Bearer tb_test_key");
        await Assert.That(request.HasBody).IsFalse();
    }

}
