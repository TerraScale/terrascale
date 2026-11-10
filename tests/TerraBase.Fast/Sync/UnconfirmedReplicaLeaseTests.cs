using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Primitives;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Sync;

public sealed class UnconfirmedReplicaLeaseTests
{
    [Test]
    public async Task A_replicated_database_refuses_writes_until_its_home_lease_is_confirmed()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var backup = new FakeS3();
        await using var api = await ManagementApi.StartAsync(
            backup.Configure,
            services =>
            {
                backup.Route(services);
                var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton(options with
                {
                    NatsUrl = new Uri("nats://127.0.0.1:9"),
                    NatsToken = "token",
                }));
            });
        var organization = await api.CreateOrganizationAsync("Org " + EntityIds.New());
        var project = await api.CreateProjectAsync(organization, "Project");
        using var owner = api.ProjectClient(project);
        using var created = await owner.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/projects/{project}/databases"),
            new { name = "replicated", allRegions = true },
            token);
        var createdBody = await created.Content.ReadAsStringAsync(token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(createdBody);
        using var listed = await owner.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), token);
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>(token);
        var id = page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "replicated").GetProperty("id").GetString()!;
        var gate = api.Server.Services.GetRequiredService<HomeLeaseGate>();
        await Assert.That(gate.Leased).Contains(id);
        await Assert.That(gate.Holds(id)).IsFalse();

        var writer = api.DataClient(await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write"));
        using var refused = await ManagementApi.SqlAsync(writer, $"/v1/projects/{project}/databases/replicated", "CREATE TABLE refused (id INTEGER)", token);
        var refusedBody = await refused.Content.ReadAsStringAsync(token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(refusedBody);
        await Assert.That(refusedBody).Contains("serialization_conflict");

        var plain = await api.CreateDatabaseAsync(organization, project, "plain");
        await Assert.That(gate.Leased).DoesNotContain(plain);
        using var accepted = await ManagementApi.SqlAsync(writer, $"/v1/projects/{project}/databases/plain", "CREATE TABLE kept (id INTEGER)", token);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(token);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(acceptedBody);

        gate.Lease(id, held: true);
        gate.Renewed(gate.Time.GetTimestamp() + (long)(gate.Time.TimestampFrequency * TimeSpan.FromMinutes(5).TotalSeconds));
        await api.Server.Services.GetRequiredService<HomeLeases>().PrepareAsync(new TenantContext(organization, PlanTier.Hobby, project, id), token);
        await Assert.That(gate.Holds(id)).IsTrue();
        using var after = await ManagementApi.SqlAsync(writer, $"/v1/projects/{project}/databases/replicated", "CREATE TABLE kept_after_prepare (id INTEGER)", token);
        var afterBody = await after.Content.ReadAsStringAsync(token);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(afterBody);
    }
}
