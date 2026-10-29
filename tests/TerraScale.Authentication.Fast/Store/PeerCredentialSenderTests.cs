using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// The regional TerraBase HttpClient that <see cref="App.Build"/> registers reads <c>TERRABASE_PEER_SECRET</c> from configuration and send it, so the TerraBase internal listener records their reads as
/// matched. The clients are the app's own, never replaced; only the origin is the test's, because the app's default origin is the
/// fixed loopback port 8081. Each test owns its TerraBase, so no other test's traffic moves the counts.
/// </summary>
public sealed class PeerCredentialSenderTests
{
    private const string RequestsMetric = "terrabase_internal_authentication_requests_total";

    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task The_storage_client_the_app_builds_sends_the_matching_peer_credential()
    {
        var (started, unavailable) = await TerraBaseTestServer.StartAsync(Token);
        await using var server = started ?? throw new InvalidOperationException($"Real TerraBase integration tests need TerraBase: {unavailable}");
        var tenant = await server.CreateTenantAsync(Token);
        var written = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
        await written.CreateAsync(Token);
        await written.CreateCollectionAsync("records", Token);
        var id = AuthStore.NewId();
        await written.PutDocumentAsync("records", id, TerraBaseFixture.Json($$"""{"marker":"{{id}}"}"""), TerraBasePrecondition.Absent, Token);

        // Built, never started: no hosted service reaches the default loopback origin.
        await using var app = App.Build(AuthOptions.Load(AuthTestServer.DefaultSettings()),
            builder => builder.Configuration["TERRABASE_PEER_SECRET"] = TerraBaseTestServer.PeerSecret);
        var client = app.Services.GetRequiredService<HttpClient>();
        var database = new TerraBaseDocumentsClient(client, new TerraBaseDocumentsClientOptions { BaseAddress = server.InternalAddress })
            .Database(tenant.Project.Id, "auth");

        using var scraper = new HttpClient { BaseAddress = server.MetricsAddress };
        var before = await CountsAsync(scraper);
        var read = await database.GetDocumentAsync("records", id, Token);
        // TerraBase's Prometheus exporter caches each scrape briefly, so the read shows up on a later one.
        var after = before;
        var counted = await ReadinessWait.UntilAsync(async () => (after = await CountsAsync(scraper)).Matched > before.Matched, TimeSpan.FromSeconds(10));

        await Assert.That(read!.Id).IsEqualTo(id);
        await Assert.That(read.Document.GetProperty("marker").GetString()).IsEqualTo(id);
        await Assert.That(counted).IsTrue();
        await Assert.That(after).IsEqualTo((before.Matched + 1, before.Unmatched));
    }

    // The matched and unmatched sums of the scrape. An absent series counts as zero.
    private static async Task<(double Matched, double Unmatched)> CountsAsync(HttpClient scraper)
    {
        var text = await scraper.GetStringAsync(new Uri("/metrics", UriKind.Relative), Token);
        return (Sum(text, "matched"), Sum(text, "unmatched"));

        static double Sum(string text, string outcome)
        {
            return text.Split('\n')
                .Where(line => line.StartsWith(RequestsMetric + "{", StringComparison.Ordinal)
                    && line.Contains($"credential=\"{outcome}\"", StringComparison.Ordinal))
                .Sum(line => double.Parse(line[(line.LastIndexOf('}') + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
        }
    }
}
