using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class PointInTimeEntitlementTests
{

    [Test]
    [Arguments("hobby", 1)]
    [Arguments("starter", 5)]
    public async Task Project_token_restores_count_toward_the_organization_database_allowance(string plan, int limit, CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new ManualTime();
        await using var api = await StartAsync(s3, time);
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        if (plan != "hobby")
        {
            await SetPlanAsync(api, organization, plan);
        }

        using var client = ProjectClient(api, organization, project);
        var database = await CreateAsync(api, client, organization, project);
        await ShipAsync(api, time, TimeSpan.FromSeconds(10), cancellationToken);
        var window = await JsonAsync(client.GetAsync(Uri(database.Data + "/point-in-time"), cancellationToken));
        var at = window.GetProperty("latest").GetDateTimeOffset();
        var route = Uri(database.Data + "/point-in-time/restore");
        for (var i = 1; i < limit - 1; i++)
        {
            await JsonAsync(client.PostAsJsonAsync(route, new { pointInTime = at, name = "restored-" + i }, cancellationToken), HttpStatusCode.Created);
        }

        if (limit > 1)
        {
            var requests = Enumerable.Range(0, 2).Select(index =>
                client.PostAsJsonAsync(route, new { pointInTime = at, name = "last-" + index }, cancellationToken));
            var responses = await Task.WhenAll(requests);
            try
            {
                await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created)).IsEqualTo(1);
                await ErrorAsync(responses.Single(response => response.StatusCode != HttpStatusCode.Created), HttpStatusCode.Conflict, "database_limit", "allows 5 databases");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }

        using var refused = await client.PostAsJsonAsync(route, new { pointInTime = at, name = "overflow" }, cancellationToken);
        await ErrorAsync(refused, HttpStatusCode.Conflict, "database_limit", "allows " + limit);
        var list = await JsonAsync(client.GetAsync(Uri($"/v1/projects/{project}/databases"), cancellationToken));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(limit);
        await Assert.That(list.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "overflow")).IsFalse();
    }

    [Test]
    [Arguments("starter", 3, 2, true)]
    [Arguments("starter", 3, 4, false)]
    [Arguments("pro", 7, 4, true)]
    [Arguments("pro", 7, 8, false)]
    public async Task Project_token_retention_uses_the_enrolled_organizations_plan(string plan, int days, int age, bool retained, CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new ManualTime();
        await using var api = await StartAsync(s3, time);
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        await SetPlanAsync(api, organization, "enterprise");
        using var client = ProjectClient(api, organization, project);
        var database = await CreateAsync(api, client, organization, project);
        await ManagementApi.CreateCollectionAsync(client, database, "people", cancellationToken);
        var document = ManagementApi.DocumentPath(database, "people", "seed");
        var text = new Faker().Lorem.Sentence();
        using (var written = await client.PutAsJsonAsync(Uri(document), new { text }, cancellationToken))
        {
            written.EnsureSuccessStatusCode();
        }

        await ShipAsync(api, time, TimeSpan.FromSeconds(10), cancellationToken);
        var at = time.GetUtcNow();
        using (var updated = await client.PutAsJsonAsync(Uri(document), new { text = text + " current" }, cancellationToken))
        {
            updated.EnsureSuccessStatusCode();
        }

        await ShipAsync(api, time, TimeSpan.FromDays(age), cancellationToken);
        var route = database.Data + "/point-in-time";
        await SetPlanAsync(api, organization, "hobby");
        await AssertWindowAsync(client, route, time.GetUtcNow().AddDays(-1), cancellationToken);
        await OutsideWindowAsync(client, route, at, cancellationToken);
        var unchanged = await JsonAsync(client.GetAsync(Uri($"/v1/projects/{project}/databases"), cancellationToken));
        await Assert.That(unchanged.GetProperty("items").GetArrayLength()).IsEqualTo(1);

        await SetPlanAsync(api, organization, plan);
        await AssertWindowAsync(client, route, retained ? at : time.GetUtcNow().AddDays(-days), cancellationToken);
        if (retained)
        {
            var preview = await JsonAsync(client.PostAsJsonAsync(Uri(route + "/plan"), new { pointInTime = at }, cancellationToken));
            await Assert.That(preview.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(at);
            await JsonAsync(client.PostAsJsonAsync(Uri(route + "/restore"), new { pointInTime = at, name = "retained" }, cancellationToken), HttpStatusCode.Created);
            var restored = await JsonAsync(client.GetAsync(Uri(document.Replace("/databases/main/", "/databases/retained/", StringComparison.Ordinal)), cancellationToken));
            await Assert.That(restored.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
        else
        {
            await OutsideWindowAsync(client, route, at, cancellationToken);
        }

        var list = await JsonAsync(client.GetAsync(Uri($"/v1/projects/{project}/databases"), cancellationToken));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(retained ? 2 : 1);
        var source = await JsonAsync(client.GetAsync(Uri(document), cancellationToken));
        await Assert.That(source.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text + " current");
    }

    private static Task<ManagementApi> StartAsync(FakeS3 s3, TimeProvider time) => ManagementApi.StartAsync(s3.Configure, services =>
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
        services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
    }, time);

    private static HttpClient ProjectClient(ManagementApi api, string organization, string project) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["org_id"] = organization;
        token.Claims["project_id"] = project;
        token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
    }));

    private static async Task<ManagedDatabaseRef> CreateAsync(ManagementApi api, HttpClient client, string organization, string project)
    {
        var created = await JsonAsync(client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }), HttpStatusCode.Created);
        var enrolled = await api.Server.Services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, CancellationToken.None);
        await Assert.That(enrolled is string id && id == organization).IsTrue();
        return new(organization, project, created.GetProperty("id").GetString()!, "main");
    }

    private static async Task SetPlanAsync(ManagementApi api, string organization, string plan)
    {
        using var response = await api.Server.Client.PutAsJsonAsync(Uri($"/admin/organizations/{organization}/plan"), new { plan });
        response.EnsureSuccessStatusCode();
    }

    private static async Task ShipAsync(ManagementApi api, ManualTime time, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        time.Advance(elapsed);
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
    }

    private static async Task AssertWindowAsync(HttpClient client, string route, DateTimeOffset earliest, CancellationToken cancellationToken)
    {
        var window = await JsonAsync(client.GetAsync(Uri(route), cancellationToken));
        await Assert.That(window.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(earliest);
    }

    private static async Task OutsideWindowAsync(HttpClient client, string route, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var operation in new[] { "plan", "restore" })
        {
            using var response = await client.PostAsJsonAsync(Uri(route + "/" + operation), new { pointInTime = at, name = "expired" }, cancellationToken);
            await ErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request", "restore window");
        }
    }

    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string message)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(error.GetProperty("message").GetString()).Contains(message);
    }

    private static async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> sending, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(status);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);

    private sealed class ManualTime : TimeProvider
    {
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

        public override long TimestampFrequency => _time.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public override long GetTimestamp() => _time.GetTimestamp();
        public void Advance(TimeSpan amount) => _time.Advance(amount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _time.CreateTimer(callback, state, period == Timeout.InfiniteTimeSpan ? dueTime : Timeout.InfiniteTimeSpan, period);
    }
}
