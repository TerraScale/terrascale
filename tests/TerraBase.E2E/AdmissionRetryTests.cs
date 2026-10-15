using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// <see cref="Docs.AdmittedAsync"/>, the retry of the suite's setup, cleanup and document calls. Against a real node's tenant
/// admission, a 429 tenant_busy (refused before the request ran) is sent again and lands once, and the wait ends with its
/// token. Any other answer is returned after one send. On Fly an e2e project the operator has not sized is hobby
/// (docs/terrabase/e2e-request-budget.md), where the suite meets these refusals; it runs in the required local stage (the local-only category).
/// </summary>
[Category("local-only")]
public sealed class AdmissionRetryTests
{
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task A_collection_create_refused_by_tenant_admission_is_sent_again_and_lands_once()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using (var database = await tenant.Data.PutAsync(tenant.Database("app"), null))
        {
            database.EnsureSuccessStatusCode();
        }

        var held = Hold(api, tenant);
        var answers = new List<HttpStatusCode>();
        try
        {
            using var created = await Docs.AdmittedAsync(async () =>
            {
                var response = await tenant.Data.PostAsJsonAsync(tenant.Database("app", "/collections"), new { name = "items" });
                answers.Add(response.StatusCode);
                Release(held);
                return response;
            });

            // Had the refused send run, the second would answer 409 name_taken.
            await Docs.EnsureSuccessAsync(created);
            await Assert.That(answers.Count).IsEqualTo(2);
            await Assert.That(answers[0]).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await Docs.StatusAsync(tenant.Data, tenant.Database("app", "/collections/items"))).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Release(held);
        }
    }

    // A retried ops PATCH serializes its ops again (each node had joined the first body: "already has a parent").
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task An_ops_patch_refused_by_tenant_admission_is_sent_again_and_applies_once()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using (var database = await tenant.Data.PutAsync(tenant.Database("app"), null))
        {
            database.EnsureSuccessStatusCode();
        }

        _ = await Docs.PostAsync(tenant.Data, tenant.Database("app", "/collections"), new JsonObject { ["name"] = "items" });
        var uri = tenant.Database("app", $"/collections/items/documents/by-key?id={EntityIds.New()}");
        await Docs.PutAsync(tenant.Data, uri, new JsonObject { ["n"] = 0 });
        var held = Hold(api, tenant);
        var answers = new List<HttpStatusCode>();
        try
        {
            using var client = new HttpClient(new Releasing(held, answers) { InnerHandler = api.Tls.CreateHandler() }) { BaseAddress = tenant.Data.BaseAddress };
            client.DefaultRequestHeaders.Authorization = tenant.Data.DefaultRequestHeaders.Authorization;
            _ = await Docs.OpsAsync(client, uri, new JsonObject { ["op"] = "inc", ["path"] = "/n", ["by"] = 1 });

            await Assert.That(answers.Count).IsEqualTo(2);
            await Assert.That(answers[0]).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await Docs.ReadAsync(tenant.Data, uri))!["n"]!.ToJsonString()).IsEqualTo("1");
        }
        finally
        {
            Release(held);
        }
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task The_wait_after_a_tenant_busy_answer_ends_with_its_token()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using (var database = await tenant.Data.PutAsync(tenant.Database("app"), null))
        {
            database.EnsureSuccessStatusCode();
        }

        var held = Hold(api, tenant);
        using var cancel = new CancellationTokenSource();
        var answers = new List<HttpStatusCode>();
        try
        {
            await Assert.That(async () => await Docs.AdmittedAsync(async () =>
            {
                var response = await tenant.Data.GetAsync(tenant.Database("app", "/collections"));
                answers.Add(response.StatusCode);
                await cancel.CancelAsync();
                return response;
            }, cancel.Token)).Throws<OperationCanceledException>();
            await Assert.That(answers).IsEquivalentTo([HttpStatusCode.TooManyRequests]);
        }
        finally
        {
            Release(held);
        }
    }

    // Only a 429 whose error.code is tenant_busy, with a positive Retry-After that fits in the deadline, is sent again.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(429, """{"error":{"code":"tenant_memory","message":"organization memory budget exceeded"}}""", 1)]
    [Arguments(429, """{"error":{"code":"other","message":"tenant_busy"}}""", 1)]
    [Arguments(429, "tenant_busy", 1)]
    [Arguments(429, """{"error":{"code":"tenant_busy"}}""", 0)]
    [Arguments(429, """{"error":{"code":"tenant_busy"}}""", 61)]
    [Arguments(429, """{"error":{"code":"tenant_busy"}}""", null)]
    [Arguments(503, """{"error":{"code":"write_outcome_unknown"}}""", 1)]
    public async Task Any_other_answer_is_returned_after_one_send(int status, string body, int? retryAfter)
    {
        var sends = 0;
        using var response = await Docs.AdmittedAsync(() =>
        {
            sends++;
            var answer = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            answer.Headers.RetryAfter = retryAfter is { } seconds ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) : null;
            return Task.FromResult(answer);
        });

        await Assert.That(sends).IsEqualTo(1);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(body);
    }

    // Every concurrency slot of the tenant's hobby organization, so its next request answers 429 tenant_busy.
    private static List<IDisposable> Hold(ManagementApi api, Tenant tenant)
    {
        var admission = api.Server.Services.GetRequiredService<IQosAdmission>();
        var organization = new TenantContext(tenant.OrganizationId, PlanTier.Hobby);
        return [.. Enumerable.Range(0, QosPolicy.For(PlanTier.Hobby).Concurrency)
            .Select(_ => admission.TryEnter(organization, out _) ?? throw new InvalidOperationException("fixture admission failed"))];
    }

    private static void Release(List<IDisposable> held)
    {
        foreach (var lease in held)
        {
            lease.Dispose();
        }

        held.Clear();
    }

    // Records each answer of the node and releases the held slots once the first is in.
    private sealed class Releasing(List<IDisposable> held, List<HttpStatusCode> answers) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            answers.Add(response.StatusCode);
            Release(held);
            return response;
        }
    }
}
