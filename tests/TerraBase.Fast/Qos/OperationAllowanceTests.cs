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

    [Test]
    public async Task The_internal_listener_lists_each_organizations_billed_units_this_month()
    {
        await using var api = await DocumentApi.StartAsync();
        (await api.PutAsync(DocumentApi.NewId(), new { text = new string('x', 9 * 1024) })).Status.ShouldBe(HttpStatusCode.Created);

        JsonElement? found = null;
        string? after = null;
        do
        {
            using var response = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/usage/allowances" + (after is null ? "" : "?after=" + after)));
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That((page.GetProperty("operationsContract").GetString(), page.GetProperty("storageContract").GetString()))
                .IsEqualTo(("operation-units-4kib-global-v1", "none"));
            found ??= page.GetProperty("items").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(item => item!.Value.GetProperty("organizationId").GetString() == api.Tenant.OrganizationId);
            after = page.GetProperty("next").GetString();
        }
        while (after is not null);

        await Assert.That(found?.GetProperty("operations").GetInt64()).IsEqualTo(3);
        using var outside = await api.Client.GetAsync(ManagementApi.Uri("/v1/usage/allowances"));
        await Assert.That(outside.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because("only the internal listener serves it");
    }
}
