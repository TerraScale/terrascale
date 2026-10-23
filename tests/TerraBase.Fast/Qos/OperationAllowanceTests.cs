using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Qos;

public sealed class OperationAllowanceTests
{
    [Test]
    public async Task Writes_stop_at_the_monthly_operation_allowance_while_deletes_and_reads_remain()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(), _ => 10)));
        var ids = Enumerable.Range(0, 11).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids[..10])
        {
            (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var refused = await api.PutAsync(ids[10], new { n = 1 });
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "operation_allowance"));
        await Assert.That(refused.Message).Contains("upgrade the plan");

        (await api.SendAsync(HttpMethod.Delete, api.Document(ids[0]))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(ids[1])).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(ids[10])).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Each_started_4_KiB_is_one_unit_and_the_allowance_counts_the_units_the_usage_api_reports()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(), _ => 30)));
        // 9,227 stored bytes: three units each, so ten writes reach the allowance of 30 that ten operations would not.
        var document = new { text = new string('x', 9 * 1024) };
        var ids = Enumerable.Range(0, 11).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids[..10])
        {
            (await api.PutAsync(id, document)).Status.ShouldBe(HttpStatusCode.Created);
        }

        var refused = await api.PutAsync(ids[10], document);
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "operation_allowance"));
        (await api.GetAsync(ids[0])).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(ids[10])).Status.ShouldBe(HttpStatusCode.NotFound);

        using var owner = api.Management.As(api.Tenant.User);
        var path = ManagementApi.Uri($"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/usage");
        var pending = (await owner.GetFromJsonAsync<JsonElement>(path)).GetProperty("month");
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        var saved = (await owner.GetFromJsonAsync<JsonElement>(path)).GetProperty("month");
        foreach (var month in new[] { pending, saved })
        {
            await Assert.That((month.GetProperty("month").GetString(), month.GetProperty("writeUnits").GetInt64(), month.GetProperty("readUnits").GetInt64(),
                month.GetProperty("units").GetInt64(), month.GetProperty("organizationUnits").GetInt64()))
                .IsEqualTo((DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), 30L, 3L, 33L, 33L))
                .Because("a not-found read and a refused write bill nothing");
        }
    }
}
