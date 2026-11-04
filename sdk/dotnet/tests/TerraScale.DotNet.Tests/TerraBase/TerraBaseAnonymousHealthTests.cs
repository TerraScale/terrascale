using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseAnonymousHealthTests
{
    [Test]
    [Arguments("Bearer synthetic-health-marker", false)]
    [Arguments(null, true)]
    [Arguments("Bearer synthetic-health-marker", true)]
    [Arguments("=synthetic-health-marker", false)]
    public async Task Health_rejects_inherited_credentials_without_dispatch_mutation_or_ownership_loss(string? authorization, bool cookie)
    {
        var calls = 0;
        var bodies = new List<StringContent>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.GetLeftPart(UriPartial.Authority) != "https://api.terrascale.tech"
                || request.Headers.Contains("Authorization") || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("Unexpected anonymous request.");
            var body = new StringContent("""{"status":"ok","version":"0","region":"gru"}""");
            bodies.Add(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        });
        using var http = new HttpClient(handler);
        var health = new TerraBaseHealthClient(http, TerraBaseRegion.PublicOrigin);
        if (authorization is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        if (authorization?.StartsWith('=') == true) await Assert.That(http.DefaultRequestHeaders.Authorization).IsNull();
        if (cookie) http.DefaultRequestHeaders.Add("Cookie", "health=synthetic-cookie-marker");
        await Assert.That(http.DefaultRequestHeaders.Contains("Authorization")).IsEqualTo(authorization is not null);
        var defaults = http.DefaultRequestHeaders.ToString();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => health.GetHealthAsync(cancelled.Token));
        for (var operation = 0; operation < 3; operation++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(health, operation));
            await Assert.That(error!.Message).Contains("credential-free HttpClient");
            await Assert.That(error.InnerException).IsNull();
            await Assert.That(error.ToString()).DoesNotContain("synthetic-health-marker");
            await Assert.That(error.ToString()).DoesNotContain("synthetic-cookie-marker");
            await Assert.That(http.DefaultRequestHeaders.ToString()).IsEqualTo(defaults);
            if (authorization is not null) await Assert.That(http.DefaultRequestHeaders.NonValidated["Authorization"].ToString()).IsEqualTo(authorization);
        }
        await Assert.That(calls).IsEqualTo(0);
        http.DefaultRequestHeaders.Remove("Authorization"); // Caller clears its own defaults.
        http.DefaultRequestHeaders.Remove("Cookie");
        for (var operation = 0; operation < 3; operation++) await Read(health, operation);
        await Assert.That(calls).IsEqualTo(3);
        foreach (var body in bodies) await Assert.ThrowsAsync<ObjectDisposedException>(() => body.ReadAsStringAsync());
        using var callerResponse = await http.GetAsync("https://api.terrascale.tech/healthz");
        await Assert.That(calls).IsEqualTo(4);
    }

    private static async Task AwaitEntryAsync(Task entered, Task pending)
    {
        var completed = await Task.WhenAny(entered, pending).WaitAsync(TimeSpan.FromSeconds(5));
        await completed; // Surface an early request failure instead of waiting for a signal that cannot arrive.
        if (completed == pending) throw new InvalidOperationException("Request completed before handler entry.");
    }

    private static Task Read(TerraBaseHealthClient health, int operation) => operation switch
    {
        0 => health.GetHealthAsync(),
        1 => health.GetReadinessAsync(),
        _ => health.GetBackupHealthAsync()
    };
}
