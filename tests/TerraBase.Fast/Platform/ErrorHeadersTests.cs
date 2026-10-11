using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;

namespace TerraBase.Fast.Platform;

public sealed class ErrorHeadersTests
{
    [Test]
    public async Task Independently_cancelled_work_returns_a_structured_uncached_error()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IShapeService, CancelledShapes>());
        using var reader = await api.ClientWithScopesAsync("shapes:read");
        using var response = await reader.GetAsync(new Uri($"{api.Shape}?offset=-1", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
    }

    [Test]
    public async Task Status_code_pages_are_never_cached_and_preserve_status_headers()
    {
        await using var api = await DocumentApi.StartAsync();
        var server = api.Server;
        using var anonymous = api.Management.DataClient();
        using var reader = await api.ClientWithScopesAsync("documents:read");
        var shape = new Uri($"{api.Shape}?offset=-1", UriKind.Relative);
        using var unauthenticated = await anonymous.GetAsync(shape);
        using var forbidden = await reader.GetAsync(shape);
        using var unknown = await server.Client.GetAsync(new Uri("/no-such-route", UriKind.Relative));
        using var wrongMethod = await server.Client.PostAsync(new Uri("/healthz", UriKind.Relative), null);

        foreach (var (response, status, code) in new[]
        {
            (unauthenticated, HttpStatusCode.Unauthorized, "invalid_key"),
            (forbidden, HttpStatusCode.Forbidden, "forbidden_scope"),
            (unknown, HttpStatusCode.NotFound, "unknown_route"),
            (wrongMethod, HttpStatusCode.MethodNotAllowed, "method_not_allowed"),
        })
        {
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        }

        await Assert.That(wrongMethod.Content.Headers.Allow).Contains("GET");
    }

    private sealed class CancelledShapes : IShapeService
    {
        public ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromCanceled<ShapeReadResult>(new CancellationToken(canceled: true));
        }
    }
}
