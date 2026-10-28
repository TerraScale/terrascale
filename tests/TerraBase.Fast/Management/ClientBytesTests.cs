using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>A database's client bytes are exactly the HTTP body bytes, however the server writes them.</summary>
public sealed class ClientBytesTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Client_bytes_are_the_exact_request_and_response_body_bytes()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        // Setup's last request records after its response arrives: start from totals that no longer move.
        var totals = await TotalsAsync(api, CancellationToken.None);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var again = await TotalsAsync(api, token);
            var settled = again == totals;
            totals = again;
            return settled;
        }, TimeSpan.FromSeconds(30));

        // A request body, and a JSON result written through the server's pipe.
        var document = JsonSerializer.SerializeToUtf8Bytes(new { name = Faker.Name.FullName(), note = Faker.Lorem.Paragraph() });
        using var content = new ByteArrayContent(document);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var (status, body) = await SendAsync(api, HttpMethod.Put, api.Document(id), content);
        await Assert.That(status).IsEqualTo(HttpStatusCode.Created);
        totals = await NextAsync(api, totals, document.Length, body.Length);

        // A body-less read sends its document and receives nothing.
        (status, body) = await SendAsync(api, HttpMethod.Get, api.Document(id));
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.Length).IsGreaterThan(document.Length);
        totals = await NextAsync(api, totals, 0, body.Length);

        // An error body written to the response stream.
        (status, body) = await SendAsync(api, HttpMethod.Get, api.Document(DocumentApi.NewId()));
        await Assert.That(status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.Length).IsGreaterThan(0);
        await NextAsync(api, totals, 0, body.Length);
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(DocumentApi api, HttpMethod method, string uri, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative)) { Content = content };
        using var response = await api.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    // Waits for the next request's record (its bytes are reported before it), then checks exactly what it added.
    private static async Task<(long Requests, long Received, long Sent)> NextAsync(
        DocumentApi api, (long Requests, long Received, long Sent) before, long received, long sent)
    {
        var after = before;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            after = await TotalsAsync(api, token);
            return after.Requests > before.Requests;
        }, TimeSpan.FromSeconds(30));
        await Assert.That(after).IsEqualTo((before.Requests + 1, before.Received + received, before.Sent + sent));
        return after;
    }

    // The database's requests and client bytes this hour and the one before (a test may cross an hour).
    private static async Task<(long Requests, long Received, long Sent)> TotalsAsync(DocumentApi api, CancellationToken cancellationToken)
    {
        var counts = await api.Server.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(await api.TenantOfAsync(cancellationToken: cancellationToken),
            new UsageWindow(UsageWindow.Hour, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600) - 1, 2), cancellationToken);
        var meters = (counts is UsageCounts usage ? usage.Lifetime : throw new InvalidOperationException("usage unavailable")).Meters?.Values.ToArray() ?? [];
        long Sum(string meter, string? kind = null)
        {
            return meters.Where(reading => reading.Dimension.Meter == meter && (kind is null || reading.Dimension.Kind == kind)).Sum(reading => reading.Count);
        }

        return (Sum("requests"), Sum("client_bytes", "received"), Sum("client_bytes", "sent"));
    }
}
