using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

/// <summary>Production outbound policy and project authorization through actual public listeners.</summary>
public sealed class WebhookSecurityTests
{
    [Test]
    public async Task Unsafe_URLs_and_any_private_DNS_answer_are_rejected_before_connecting()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        foreach (var url in new[]
        {
            "not a URL", "http://webhook-receiver.test/events", "https://webhook-receiver.test:8443/events",
            "https://user:password@webhook-receiver.test/events", "https://webhook-receiver.test/events#fragment",
            "https://127.0.0.1/events", "https://10.1.2.3/events", "https://172.16.1.2/events",
            "https://192.168.1.2/events", "https://169.254.169.254/events", "https://0.0.0.0/events",
            "https://224.0.0.1/events", "https://[::1]/events", "https://[::]/events", "https://[fc00::1]/events",
            "https://[fe80::1]/events", "https://[::ffff:127.0.0.1]/events", "https://[::ffff:10.1.2.3]/events",
            "https://[2002:0a00:0001::1]/events", "https://[2001:db8::1]/events",
            "https://webhook-receiver.test/" + new string('x', 2048),
        })
        {
            using var response = await fixture.Client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because(url);
        }

        foreach (var addresses in (string[][])
        [
            [], ["10.0.0.1"], ["::1"], ["::ffff:192.168.1.1"],
            ["93.184.216.34", "169.254.169.254"], ["2606:4700:4700::1111", "fc00::1"],
        ])
        {
            fixture.Receiver.Addresses(addresses);
            using var response = await fixture.Client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url = WebhookReceiver.Url }, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        await Assert.That(fixture.Receiver.Connections).IsEqualTo(0);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(0);
        await Assert.That((await fixture.JsonAsync(fixture.Path, token)).GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task DNS_rebinding_is_revalidated_at_send_and_the_rejected_attempt_can_retry_safely()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        fixture.Receiver.Addresses("93.184.216.34", "10.0.0.1");
        await fixture.PutAsync("rebind", token);
        await fixture.DriveAfterCommitAsync(async cancellation =>
        {
            var list = await fixture.JsonAsync($"{fixture.Path}/{id}/deliveries", cancellation);
            return list.GetArrayLength() == 1 && list[0].GetProperty("errorCode").GetString() == "target_rejected";
        }, token);
        var rejected = await fixture.WaitForDeliveryAsync(id, "pending", token, "target_rejected");
        await Assert.That(rejected.GetProperty("httpStatus").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(rejected.GetProperty("attempts").GetInt64()).IsEqualTo(1);
        await Assert.That(fixture.Receiver.Connections).IsEqualTo(0);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(0);

        fixture.Receiver.Addresses("93.184.216.34");
        var captured = await fixture.WaitForRequestAsync(token);
        await Assert.That(captured.Delivery).IsEqualTo(rejected.GetProperty("id").GetString());
        await WebhookDeliveryTests.SignedAsync(captured, hook.GetProperty("secret").GetString()!);
        var delivered = await fixture.WaitForDeliveryAsync(id, "delivered", token);
        await Assert.That(delivered.GetProperty("attempts").GetInt64()).IsEqualTo(2);
        await Assert.That(fixture.Receiver.Connections).IsEqualTo(1);
        await Assert.That(fixture.Receiver.UsesProxy).IsFalse();
    }

    [Test]
    public async Task A_redirect_is_recorded_as_failure_and_never_followed()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        fixture.Receiver.FirstStatus = 302;
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        await fixture.PutAsync("redirect", token);
        var first = await fixture.WaitForRequestAsync(token);
        var rejected = await fixture.WaitForDeliveryAsync(id, "pending", token, "http_error");
        await Assert.That(rejected.GetProperty("httpStatus").GetInt32()).IsEqualTo(302);
        await Assert.That(fixture.Receiver.RedirectsFollowed).IsEqualTo(0);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(1);
        await Assert.That(fixture.Receiver.FollowsRedirects).IsFalse();
        var retried = await fixture.WaitForRequestAsync(token);
        await Assert.That(retried.Delivery).IsEqualTo(first.Delivery);
        await Assert.That(retried.Body.SequenceEqual(first.Body)).IsTrue();
        _ = await fixture.WaitForDeliveryAsync(id, "delivered", token);
        await Assert.That(fixture.Receiver.RedirectsFollowed).IsEqualTo(0);
    }

    [Test]
    public async Task Existing_document_scopes_are_required_and_legacy_keys_cannot_manage_webhooks()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        using var read = fixture.ProjectClient("documents:read");
        using var write = fixture.ProjectClient("documents:write");
        using var noScopes = fixture.ProjectClient("");
        using var anonymous = fixture.Api.WithToken(null);
        using var legacy = await fixture.Api.CreateClientAsync(fixture.Database.Organization, "documents:read", "documents:write");

        using (var list = await read.GetAsync(ManagementApi.Uri(fixture.Path), token))
        {
            await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using (var history = await read.GetAsync(ManagementApi.Uri($"{fixture.Path}/{id}/deliveries"), token))
        {
            await Assert.That(history.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        foreach (var client in new[] { read, write, noScopes })
        {
            using var create = await client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url = WebhookReceiver.Url }, token);
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var delete = await client.DeleteAsync(ManagementApi.Uri($"{fixture.Path}/{id}"), token);
            await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        foreach (var client in new[] { write, noScopes })
        {
            using var list = await client.GetAsync(ManagementApi.Uri(fixture.Path), token);
            await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var history = await client.GetAsync(ManagementApi.Uri($"{fixture.Path}/{id}/deliveries"), token);
            await Assert.That(history.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        foreach (var client in new[] { legacy, anonymous })
        {
            using var list = await client.GetAsync(ManagementApi.Uri(fixture.Path), token);
            await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var create = await client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url = WebhookReceiver.Url }, token);
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // Client endpoints are hidden from the internal listener.
        using var wrongListener = await fixture.Api.Server.Client.GetAsync(ManagementApi.Uri(fixture.Path), token);
        await Assert.That(wrongListener.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Webhook_ids_do_not_cross_project_database_collection_or_database_token_binding()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        var sibling = await fixture.Api.CreateManagedDatabaseAsync(fixture.Database.Organization, fixture.Database.Project, "sibling");
        await ManagementApi.CreateCollectionAsync(fixture.Client, sibling, "items", token);
        await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "other", token);
        foreach (var path in new[] { WebhookApi.Route(sibling), WebhookApi.Route(fixture.Database, "other") })
        {
            await Assert.That((await fixture.JsonAsync(path, token)).GetArrayLength()).IsEqualTo(0);
            using var history = await fixture.Client.GetAsync(ManagementApi.Uri($"{path}/{id}/deliveries"), token);
            await Assert.That(history.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var delete = await fixture.Client.DeleteAsync(ManagementApi.Uri($"{path}/{id}"), token);
            await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var foreign = await fixture.Api.CreateManagedDatabaseAsync("foreign");
        await fixture.Api.VerifyProjectBindingAsync(foreign.Organization, foreign.Project);
        using var otherProject = fixture.ProjectClient(project: foreign.Project);
        await ManagementApi.CreateCollectionAsync(otherProject, foreign, "items", token);
        using var databaseBound = fixture.ProjectClient(database: sibling.Id);
        foreach (var client in new[] { otherProject, databaseBound })
        {
            using var list = await client.GetAsync(ManagementApi.Uri(fixture.Path), token);
            await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var history = await client.GetAsync(ManagementApi.Uri($"{fixture.Path}/{id}/deliveries"), token);
            await Assert.That(history.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var create = await client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url = WebhookReceiver.Url }, token);
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var foreignRead = await fixture.Client.GetAsync(ManagementApi.Uri(WebhookApi.Route(foreign)), token);
        await Assert.That(foreignRead.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var missing = await fixture.Client.PostAsJsonAsync(ManagementApi.Uri(WebhookApi.Route(fixture.Database, "missing")),
            new { url = WebhookReceiver.Url }, token);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await fixture.JsonAsync(WebhookApi.Route(fixture.Database, "missing"), token)).GetArrayLength()).IsEqualTo(0);
        using var absentHook = await fixture.Client.GetAsync(ManagementApi.Uri($"{fixture.Path}/{EntityIds.New()}/deliveries"), token);
        await Assert.That(absentHook.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignWrite = await otherProject.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(foreign, "items", "excluded-proj")),
            new { privateValue = "another-project" }, token);
        foreignWrite.EnsureSuccessStatusCode();
        await fixture.PutAsync("excluded-db", token, sibling);
        await fixture.PutAsync("excluded-col", token, collection: "other");
        await fixture.PutAsync("included", token);
        var delivered = await fixture.WaitForRequestAsync(token);
        using var payload = JsonDocument.Parse(delivered.Body);
        var events = payload.RootElement.GetProperty("events");
        await Assert.That(events.GetArrayLength()).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("included"));
    }
}
