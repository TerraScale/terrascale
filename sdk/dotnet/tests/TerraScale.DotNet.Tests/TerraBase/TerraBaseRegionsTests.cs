using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests.TerraBase;

using TerraScale.DotNet.TerraBase;

public sealed class TerraBaseRegionsTests
{
    [Test]
    public async Task Catalog_is_anonymous_exact_and_not_cached_or_used_for_routing()
    {
        var calls = 0;
        var bodies = new List<StringContent>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsoluteUri != "https://api.terrascale.tech/v1/regions"
                || request.Content is not null || request.Headers.Authorization is not null || request.Headers.Contains(TerraBaseRegion.PinHeader)
                || calls <= 2 && request.Headers.Accept.Single().MediaType != "application/json")
                throw new InvalidOperationException("Unexpected catalog request.");
            var body = new StringContent(calls == 1 ? """{"regions":["fra","gru","new-region9","sjc"]}""" : """{"regions":["gru"]}""", Encoding.UTF8, "application/json");
            bodies.Add(body);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
            response.Headers.CacheControl = new() { Public = true, MaxAge = TimeSpan.FromSeconds(5) };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseRegionsClient(http);
        await Assert.That((await client.ListRegionsAsync()).Regions).IsEquivalentTo(["fra", "gru", "new-region9", "sjc"]);
        await Assert.That((await client.ListRegionsAsync()).Regions).IsEquivalentTo(["gru"]);
        await Assert.That(calls).IsEqualTo(2);
        foreach (var body in bodies) await Assert.ThrowsAsync<ObjectDisposedException>(() => body.ReadAsStringAsync());
        // The SDK did not dispose the caller's HttpClient.
        using var callerResponse = await http.GetAsync("https://api.terrascale.tech/v1/regions");
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    [Arguments("Authorization", "Bearer synthetic-catalog-test")]
    [Arguments("Cookie", "catalog=synthetic-test")]
    public async Task Caller_default_credentials_are_rejected_without_dispatch_or_mutation(string header, string value)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.AbsoluteUri != "https://api.terrascale.tech/v1/regions" || request.Headers.Contains("Authorization") || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("Anonymous request leaked credentials.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"regions":["gru"]}""") });
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseRegionsClient(http);
        http.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListRegionsAsync());
        await Assert.That(error!.Message).Contains("credential-free HttpClient");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(http.DefaultRequestHeaders.GetValues(header).Single()).IsEqualTo(value);
        http.DefaultRequestHeaders.Remove(header); // Caller owns and clears its header.
        await Assert.That((await client.ListRegionsAsync()).Regions).IsEquivalentTo(["gru"]);
    }

    [Test]
    public async Task In_flight_cancellation_reaches_the_transport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TerraBaseTestHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled request should not complete.");
        });
        using var http = new HttpClient(handler);
        using var cancelled = new CancellationTokenSource();
        var pending = new TerraBaseRegionsClient(http).ListRegionsAsync(cancelled.Token);
        try
        {
            var completed = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            await completed; // Surface a request failure before handler entry.
            if (completed == pending) throw new InvalidOperationException("Request completed before handler entry.");
            await cancelled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await cancelled.CancelAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("{\"regions\":null}")]
    [Arguments("{\"regions\":[]}")]
    [Arguments("{\"regions\":[null]}")]
    [Arguments("{\"regions\":[\"GRU\"]}")]
    [Arguments("{\"regions\":[\"gru\",\"gru\"]}")]
    [Arguments("{\"regions\":[\"sjc\",\"gru\"]}")]
    public async Task Invalid_catalogs_are_rejected(string body)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => new TerraBaseRegionsClient(http).ListRegionsAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(404)]
    [Arguments(503)]
    public async Task Errors_are_preserved_once_and_precancellation_sends_nothing(int status)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("""{"error":{"code":"unavailable","message":"unavailable"}}""") });
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseRegionsClient(http);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.ListRegionsAsync());
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo("unavailable");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ListRegionsAsync(cancelled.Token));
        await Assert.That(calls).IsEqualTo(1);
    }
}
