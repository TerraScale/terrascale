using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>DeleteAsync over a fake handler.</summary>
public sealed class TerraBaseDatabaseDeleteTests
{
    private static readonly TerraBaseDocumentsClientOptions Options = new()
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
        var database = new TerraBaseDocumentsClient(http, Options).Database("project", "app");

        await database.DeleteAsync();

        var request = sent.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.Uri).IsEqualTo("https://terrabase.example.test/v1/projects/project/databases/app");
        await Assert.That(request.Authorization).IsEqualTo("Bearer tb_test_key");
        await Assert.That(request.HasBody).IsFalse();
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound, "unknown_database", typeof(TerraBaseNotFoundException))]
    [Arguments(HttpStatusCode.Conflict, "database_replicated", typeof(TerraBaseConflictException))]
    public async Task DeleteFailuresKeepTheirStatusAndErrorCode(HttpStatusCode status, string code, Type type)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"refused\"}}}}"),
        }));
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, Options).Database("project", "app");

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => database.DeleteAsync());

        await Assert.That(error!.GetType()).IsEqualTo(type);
        await Assert.That(error.StatusCode).IsEqualTo(status);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
    }

    [Test]
    public async Task ClientsWithoutCredentialSendNoAuthorization()
    {
        string? authorization = "unset";
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = new Uri("http://100.64.0.1:8081/") })
            .Database("project", "app");

        await database.DeleteAsync();

        await Assert.That(authorization).IsNull();
    }
}
