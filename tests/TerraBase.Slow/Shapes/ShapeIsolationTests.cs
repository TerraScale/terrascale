using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Slow.Documents;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

/// <summary>
/// Tenant isolation of live tails over HTTP (free-reads.md 3.6): one organization's parked long polls fill its own share of
/// the shape capacity and no more, so another organization on the same server still reads. The flights run on a fake
/// clock, so no wait ends by time; each parked reader is admitted only after the previous one is parked.
/// </summary>
public sealed class ShapeIsolationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task An_organization_parked_at_its_requester_share_is_refused_while_another_organization_reads()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start));
        // Enterprise has no request-rate plan limit, so a full requester share of polls reaches the flights.
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var token = TestContext.Current!.Execution.CancellationToken;
        var shape = ShapeOf(api.Tenant.ProjectId);
        var (handle, offset) = await SyncAsync(api.Client, shape, token);
        var live = $"{shape}?handle={handle}&offset={offset}&live=true";

        // Every reader, each on its own cursor, shares one flight and one requester share of the organization.
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(token);
        var parked = await ParkTogetherAsync(api, ShapeFlights<ShapeRead>.MaxRequestersPerOrganization, cursor => $"{live}&cursor={cursor}", leaving.Token);

        var refused = await DocumentApi.SendAsync(api.Client, HttpMethod.Get, live, cancellationToken: token);
        await Assert.That(refused.Status).IsEqualTo(HttpStatusCode.TooManyRequests).Because("the requester past the organization's share is refused");
        await Assert.That(refused.Code).IsEqualTo("shape_busy");

        await ReadOtherOrganizationAsync(api, token);

        // Leaving cancels each parked poll on the client, and the server's last requester of each flight cancels it too.
        leaving.Cancel();
        foreach (var request in parked)
        {
            await Assert.That(async () => await request).Throws<TaskCanceledException>();
        }
    }

    [Test]
    public async Task An_organization_at_its_flight_share_is_refused_over_http_while_another_organization_reads()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start));
        var token = TestContext.Current!.Execution.CancellationToken;
        var shape = ShapeOf(api.Tenant.ProjectId);

        // Each filter is its own handle and so its own flight: one parked reader per filter fills the organization's flight share.
        var lives = new List<string>();
        for (var n = 0; n <= ShapeFlights<ShapeRead>.MaxFlightsPerOrganization; n++)
        {
            var where = $"where={Uri.EscapeDataString($$"""{"eq":["/n",{{n}}]}""")}";
            var (handle, offset) = await SyncAsync(api.Client, $"{shape}?{where}", token);
            lives.Add($"{shape}?{where}&handle={handle}&offset={offset}&live=true");
        }

        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(token);
        var parked = await ParkAsync(api, ShapeFlights<ShapeRead>.MaxFlightsPerOrganization, n => lives[n - 1], leaving.Token);

        var refused = await DocumentApi.SendAsync(api.Client, HttpMethod.Get, lives[^1], cancellationToken: token);
        await Assert.That(refused.Status).IsEqualTo(HttpStatusCode.TooManyRequests).Because("the organization's ninth flight is over its share");
        await Assert.That(refused.Code).IsEqualTo("shape_busy");

        await ReadOtherOrganizationAsync(api, token);

        leaving.Cancel();
        foreach (var request in parked)
        {
            await Assert.That(async () => await request).Throws<TaskCanceledException>();
        }
    }

    private static Task<DocumentApi> StartAsync(FakeTimeProvider time) =>
        DocumentApi.StartAsync(services: collection => collection.AddSingleton<IShapeFlights<ShapeRead>>(provider =>
            new ShapeFlights<ShapeRead>(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<IShapeFlightSealer>(),
                provider.GetRequiredService<IMemoryBudget>(), time, provider.GetRequiredService<IHostApplicationLifetime>(),
                provider.GetRequiredService<IShapeMetrics>())));

    // Parks <paramref name="count"/> live readers, admitting each only after the previous one is counted as waiting.
    private static async Task<List<Task<HttpResponseMessage>>> ParkAsync(DocumentApi api, int count, Func<int, string> target, CancellationToken cancellationToken)
    {
        var parked = new List<Task<HttpResponseMessage>>();
        for (var n = 1; n <= count; n++)
        {
            parked.Add(api.Client.GetAsync(target(n), cancellationToken));
            var admitted = n;
            await TerraBaseTestServer.EventuallyAsync(async ct =>
                parked.Any(request => request.IsCompleted) || await WaitingAsync(api.Server, ct) >= admitted);
            foreach (var completed in parked.Where(request => request.IsCompleted))
            {
                using var early = await completed;
                throw new InvalidOperationException($"reader answered before all {count} parked: {early.StatusCode}, {await early.Content.ReadAsStringAsync(cancellationToken)}");
            }
        }

        return parked;
    }

    // Parks <paramref name="count"/> live readers of one flight at once, then waits until every one is counted as waiting.
    private static async Task<List<Task<HttpResponseMessage>>> ParkTogetherAsync(DocumentApi api, int count, Func<int, string> target, CancellationToken cancellationToken)
    {
        List<Task<HttpResponseMessage>> parked = [.. Enumerable.Range(1, count).Select(n => api.Client.GetAsync(target(n), cancellationToken))];
        await TerraBaseTestServer.EventuallyAsync(async ct =>
            parked.Any(request => request.IsCompleted) || await WaitingAsync(api.Server, ct) >= count);
        foreach (var completed in parked.Where(request => request.IsCompleted))
        {
            using var early = await completed;
            throw new InvalidOperationException($"reader answered before all {count} parked: {early.StatusCode}, {await early.Content.ReadAsStringAsync(cancellationToken)}");
        }

        return parked;
    }

    // The waiting gauge has no sample until the first reader parks, so an absent gauge reads as zero.
    private static async Task<double> WaitingAsync(TerraBaseTestServer server, CancellationToken token)
    {
        var scrape = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);
        return scrape.Split('\n')
            .Where(line => line.StartsWith("terrabase_shape_requests_waiting ", StringComparison.Ordinal)
                || line.StartsWith("terrabase_shape_requests_waiting{", StringComparison.Ordinal))
            .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));
    }

    // A second organization on the same server bootstraps and reads at the sealed head.
    private static async Task ReadOtherOrganizationAsync(DocumentApi api, CancellationToken token)
    {
        using var other = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        var created = await DocumentApi.SendAsync(other.Data, HttpMethod.Put, $"/v1/projects/{other.ProjectId}/databases/{DocumentApi.Database}", cancellationToken: token);
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var collection = await DocumentApi.SendAsync(other.Data, HttpMethod.Post,
            $"/v1/projects/{other.ProjectId}/databases/{DocumentApi.Database}/collections", JsonContent.Create(new { name = DocumentApi.Collection }), cancellationToken: token);
        await Assert.That(collection.Status).IsEqualTo(HttpStatusCode.Created);
        var document = await DocumentApi.SendAsync(other.Data, HttpMethod.Put,
            $"/v1/projects/{other.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/documents/by-key?id={DocumentApi.NewId()}",
            JsonContent.Create(new { status = "open" }), cancellationToken: token);
        await Assert.That(document.Status).IsEqualTo(HttpStatusCode.Created);
        await SyncAsync(other.Data, ShapeOf(other.ProjectId), token);
    }

    private static string ShapeOf(string projectId) =>
        $"/v1/projects/{projectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/shape";

    // Bootstraps the newest snapshot, then reads closed chunks until the sealed head, and returns the handle and that head's offset.
    private static async Task<(string Handle, string Offset)> SyncAsync(HttpClient client, string shape, CancellationToken token)
    {
        var query = shape.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using var bootstrap = await client.GetAsync($"{shape}{query}offset=-1", token);
        await Assert.That(bootstrap.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        using var page = await client.GetAsync(bootstrap.Headers.Location, token);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var offset = page.Headers.GetValues("TB-Shape-Next-Offset").Single();
        while (offset.StartsWith("snap.", StringComparison.Ordinal))
        {
            using var next = await client.GetAsync($"{shape}{query}handle={handle}&offset={offset}", token);
            await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
            offset = next.Headers.GetValues("TB-Shape-Next-Offset").Single();
        }

        string upToDate;
        do
        {
            using var chunk = await client.GetAsync($"{shape}{query}handle={handle}&offset={offset}", token);
            await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.OK);
            offset = chunk.Headers.GetValues("TB-Shape-Next-Offset").Single();
            upToDate = chunk.Headers.GetValues("TB-Shape-Up-To-Date").Single();
        }
        while (upToDate != "true");
        return (handle, offset);
    }
}
