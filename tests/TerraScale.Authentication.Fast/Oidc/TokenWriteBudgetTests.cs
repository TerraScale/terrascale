using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>
/// Every non-limiter TerraBase write waits for another region's durable ack, so a token request's latency is mostly
/// its count of sequential writes. Each issued entry is stored with its ancestry in the write that creates it.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenWriteBudgetTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_refresh_redeems_and_issues_bound_tokens_in_five_writes()
    {
        using var writes = new CountingWrites { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(writes);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(http));
        var refresh = (await oidc.SignInAndRedeemAsync()).Tokens.GetProperty("refresh_token").GetString()!;

        writes.Reset();
        using var refreshed = await oidc.RefreshAsync(refresh);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // Redemption, then the access, refresh (create and reference payload) and identity token entries.
        await Assert.That(writes.Count).IsEqualTo(5);

        var issued = (await oidc.Database.ListDocumentsAsync("oidc_tokens", limit: 1000)).Items.Select(item => item.Document)
            .Where(token => token.GetProperty("status").GetString() == "valid").ToList();
        await Assert.That(issued.Count).IsGreaterThanOrEqualTo(3);
        foreach (var token in issued)
        {
            await Assert.That(token.GetProperty("bindings").GetArrayLength()).IsGreaterThan(0);
        }
    }

    // Counts writes to the OIDC collections (document writes and transactions touching them); background workers and
    // rate limits write elsewhere.
    private sealed class CountingWrites : DelegatingHandler
    {
        private int count;

        public int Count => Volatile.Read(ref this.count);

        public void Reset() => Volatile.Write(ref this.count, 0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get && !path.EndsWith("/query", StringComparison.Ordinal)
                && (path.Contains("/collections/oidc_", StringComparison.Ordinal)
                    || (path.EndsWith("/transactions", StringComparison.Ordinal) && request.Content is not null
                        && (await request.Content.ReadAsStringAsync(cancellationToken)).Contains("\"oidc_", StringComparison.Ordinal))))
            {
                Interlocked.Increment(ref this.count);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
