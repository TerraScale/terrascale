using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

/// <summary>Document writes reach a real HTTPS receiver through the public client listener.</summary>
public sealed class WebhookDeliveryTests
{
    [Test]
    public async Task Receiver_success_with_a_failed_acknowledgment_is_replayed_after_restart_without_skipping_the_next_event()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var hook = new UsageCommitHook((collection, _, json) =>
        {
            if (!collection.Contains("/webhook_deliveries/", StringComparison.Ordinal))
            {
                return false;
            }

            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.GetProperty("details").GetProperty("status").GetString() == "delivered";
        });
        var failureLog = new WebhookFailureLog();
        await using var fixture = await WebhookApi.StartAsync(services =>
        {
            hook.Install(services);
            TerraBaseTestServer.CaptureDebugLogs(services, failureLog);
        }, token);
        var registration = await fixture.RegisterAsync(token);
        var id = registration.GetProperty("id").GetString()!;
        using var failure = hook.PauseCommit(fail: true);
        await fixture.PutAsync("ack-failed", token);
        var accepted = await fixture.WaitForRequestAsync(token);
        await failure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        // The real receiver returned 204; fail only the durable acknowledgment transaction.
        failure.Dispose();
        await failureLog.Failed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        var pending = await fixture.WaitForDeliveryAsync(id, "pending", token);
        await Assert.That(pending.GetProperty("attempts").GetInt64()).IsEqualTo(1);
        await Assert.That(pending.GetProperty("httpStatus").ValueKind).IsEqualTo(JsonValueKind.Null);
        await fixture.PutAsync("after-ack", token);
        await fixture.StopAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(12));
        await fixture.RestartAsync(token);

        var replay = await fixture.WaitForRequestAsync(token);
        await Assert.That(replay.Delivery).IsEqualTo(accepted.Delivery);
        await Assert.That(replay.Body.SequenceEqual(accepted.Body)).IsTrue();
        await SignedAsync(replay, registration.GetProperty("secret").GetString()!);
        _ = await fixture.WaitForDeliveryAsync(id, "delivered", token);
        var later = await fixture.WaitForRequestAsync(token);
        using var body = JsonDocument.Parse(later.Body);
        await Assert.That(later.Delivery).IsNotEqualTo(accepted.Delivery);
        await Assert.That(body.RootElement.GetProperty("events").GetArrayLength()).IsEqualTo(1);
        await Assert.That(body.RootElement.GetProperty("events")[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("after-ack"));
        await Assert.That(fixture.Receiver.Received).IsEqualTo(3);
    }

    [Test]
    public async Task Creation_cannot_retain_a_signing_secret_after_database_deletion()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var hook = new UsageCommitHook();
        await using var fixture = await WebhookApi.StartAsync(hook.Install, token);
        using var gate = hook.PauseWrite();
        var creation = fixture.Client.PostAsJsonAsync(ManagementApi.Uri(fixture.Path), new { url = WebhookReceiver.Url }, token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        using (var deleted = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            deleted.EnsureSuccessStatusCode();
        }

        gate.Dispose();
        using var response = await creation.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await system.BeginTransactionAsync(write: false, token);
        foreach (var collection in new[]
        {
            $"databases/{fixture.Database.Id}/webhooks/items", $"databases/{fixture.Database.Id}/webhook_state", "webhook_dispatch",
        })
        {
            var page = await read.ListAsync(collection, null, 1, 1024, token);
            await Assert.That(page.Documents.Count).IsEqualTo(0);
        }
        await Assert.That(fixture.Receiver.Received).IsEqualTo(0);
    }

    [Test]
    public async Task A_retry_preserves_the_signed_raw_body_without_backfilling_or_exposing_secrets()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        fixture.Receiver.FirstStatus = 503;
        await fixture.PutAsync("before", token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        var secret = hook.GetProperty("secret").GetString()!;
        await Assert.That(Base64Url.DecodeFromChars(secret).Length).IsEqualTo(32);
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var registeredVersion = await RegistrationVersionAsync();
        await Assert.That(registeredVersion).IsNotNull();
        await fixture.PutAsync("after", token);

        var first = await fixture.WaitForRequestAsync(token);
        await SignedAsync(first, secret);
        using var payload = JsonDocument.Parse(first.Body);
        await Assert.That(payload.RootElement.GetProperty("id").GetString()).IsEqualTo(first.Delivery);
        await Assert.That(payload.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(id);
        await Assert.That(payload.RootElement.GetProperty("projectId").GetString()).IsEqualTo(fixture.Database.Project);
        await Assert.That(payload.RootElement.GetProperty("database").GetString()).IsEqualTo(fixture.Database.Name);
        await Assert.That(payload.RootElement.GetProperty("collection").GetString()).IsEqualTo("items");
        var events = payload.RootElement.GetProperty("events");
        await Assert.That(events.GetArrayLength()).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("after"));
        await Assert.That(events[0].GetProperty("deleted").GetBoolean()).IsFalse();
        await Assert.That(events[0].GetProperty("origin").GetString()!.Length).IsEqualTo(16);
        await Assert.That(events[0].GetProperty("version").GetString()!.Length).IsEqualTo(16);
        await Assert.That(Encoding.UTF8.GetString(first.Body).Contains("privateValue", StringComparison.Ordinal)).IsFalse();

        var failed = await fixture.WaitForDeliveryAsync(id, "pending", token, "http_error");
        await Assert.That(failed.GetProperty("attempts").GetInt64()).IsEqualTo(1);
        await Assert.That(failed.GetProperty("httpStatus").GetInt32()).IsEqualTo(503);
        await Assert.That(failed.GetProperty("nextAttemptAt").GetDateTimeOffset()
            - failed.GetProperty("lastAttemptAt").GetDateTimeOffset()).IsEqualTo(TimeSpan.FromSeconds(1));
        var second = await fixture.WaitForRequestAsync(token);
        await SignedAsync(second, secret);
        await Assert.That(second.Delivery).IsEqualTo(first.Delivery);
        await Assert.That(second.Body.SequenceEqual(first.Body)).IsTrue();
        await Assert.That(long.Parse(second.Timestamp, CultureInfo.InvariantCulture)).IsGreaterThan(long.Parse(first.Timestamp, CultureInfo.InvariantCulture));
        var delivered = await fixture.WaitForDeliveryAsync(id, "delivered", token);
        await Assert.That(delivered.GetProperty("attempts").GetInt64()).IsEqualTo(2);
        await Assert.That(delivered.GetProperty("httpStatus").GetInt32()).IsEqualTo(204);
        await Assert.That(delivered.GetProperty("errorCode").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(delivered.GetProperty("nextAttemptAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(await RegistrationVersionAsync()).IsEqualTo(registeredVersion);

        var listed = await fixture.JsonAsync(fixture.Path, token);
        await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed[0].TryGetProperty("secret", out _)).IsFalse();
        await Assert.That(delivered.TryGetProperty("body", out _)).IsFalse();
        foreach (var text in new[] { listed.GetRawText(), delivered.GetRawText(), fixture.Logs.Text })
        {
            await Assert.That(text.Contains(secret, StringComparison.Ordinal)).IsFalse();
            await Assert.That(text.Contains(first.Signature, StringComparison.Ordinal)).IsFalse();
            await Assert.That(text.Contains(WebhookReceiver.ResponseMarker, StringComparison.Ordinal)).IsFalse();
        }

        var document = await fixture.JsonAsync(ManagementApi.DocumentPath(fixture.Database, "items", "after"), token);
        await Assert.That(document.GetRawText().Contains(secret, StringComparison.Ordinal)).IsFalse();
        var collections = await fixture.JsonAsync(fixture.Database.Data + "/collections", token);
        await Assert.That(collections.GetRawText().Contains("webhook", StringComparison.OrdinalIgnoreCase)).IsFalse();
        foreach (var collection in new[] { "webhook_dispatch", "webhooks", "webhook_deliveries" })
        {
            using var hidden = await fixture.Client.GetAsync(ManagementApi.Uri(
                $"{fixture.Database.Data}/collections/{collection}/documents/by-key?id={id}"), token);
            await Assert.That(hidden.IsSuccessStatusCode).IsFalse();
            await Assert.That((await hidden.Content.ReadAsStringAsync(token)).Contains(secret, StringComparison.Ordinal)).IsFalse();
        }

        var deleteEndpoint = fixture.Api.Server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText!.EndsWith("/webhooks/{webhook}", StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
        await Assert.That(deleteEndpoint.Metadata.GetMetadata<AnchoredRevocation>() is not null).IsTrue();
        using var deleted = await fixture.Client.DeleteAsync(ManagementApi.Uri($"{fixture.Path}/{id}"), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var control = await fixture.RegisterAsync(token);
        await fixture.PutAsync("after-delete", token);
        var afterDelete = await fixture.WaitForRequestAsync(token);
        using var controlPayload = JsonDocument.Parse(afterDelete.Body);
        await Assert.That(controlPayload.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(control.GetProperty("id").GetString());
        _ = await fixture.WaitForDeliveryAsync(control.GetProperty("id").GetString()!, "delivered", token);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(3);
        await Assert.That((await fixture.JsonAsync(fixture.Path, token)).GetArrayLength()).IsEqualTo(1);
        using var deletedHistory = await fixture.Client.GetAsync(ManagementApi.Uri($"{fixture.Path}/{id}/deliveries"), token);
        await Assert.That(deletedHistory.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        async ValueTask<DocumentVersion?> RegistrationVersionAsync()
        {
            await using var read = await system.BeginTransactionAsync(write: false, token);
            return await read.GetVersionAsync($"databases/{fixture.Database.Id}/webhooks/items", id, token);
        }
    }

    [Test]
    public async Task Pending_delivery_resumes_after_restart_with_the_same_id_body_and_signing_key()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        fixture.Receiver.FirstStatus = 503;
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        var secret = hook.GetProperty("secret").GetString()!;
        await fixture.PutAsync("restart", token);
        var first = await fixture.WaitForRequestAsync(token);
        _ = await fixture.WaitForDeliveryAsync(id, "pending", token, "http_error");
        await fixture.RestartAsync(token);

        var replay = await fixture.WaitForRequestAsync(token);
        await SignedAsync(replay, secret);
        await Assert.That(replay.Delivery).IsEqualTo(first.Delivery);
        await Assert.That(replay.Body.SequenceEqual(first.Body)).IsTrue();
        var delivered = await fixture.WaitForDeliveryAsync(id, "delivered", token);
        await Assert.That(delivered.GetProperty("attempts").GetInt64()).IsEqualTo(2);
        await Assert.That((await fixture.JsonAsync(fixture.Path, token))[0].GetProperty("id").GetString()).IsEqualTo(id);

        // A new post-restart write proves the durable cursor does not replay the acknowledged document.
        using var removed = await fixture.Client.DeleteAsync(ManagementApi.Uri(
            ManagementApi.DocumentPath(fixture.Database, "items", "restart")), token);
        removed.EnsureSuccessStatusCode();
        var deletion = await fixture.WaitForRequestAsync(token);
        using var body = JsonDocument.Parse(deletion.Body);
        var changes = body.RootElement.GetProperty("events");
        await Assert.That(changes.GetArrayLength()).IsEqualTo(1);
        await Assert.That(changes[0].GetProperty("deleted").GetBoolean()).IsTrue();
        await Assert.That(changes[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("restart"));
        await Assert.That(deletion.Delivery).IsNotEqualTo(first.Delivery);
    }

    [Test]
    public async Task A_different_machine_does_not_take_over_the_original_machines_pending_delivery()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        fixture.Receiver.FirstStatus = 503;
        var original = await fixture.RegisterAsync(token);
        var originalId = original.GetProperty("id").GetString()!;
        await fixture.PutAsync("owned", token);
        var pending = await fixture.WaitForRequestAsync(token);
        _ = await fixture.WaitForDeliveryAsync(originalId, "pending", token, "http_error");
        await fixture.RestartAsync(token, differentMachine: true);
        var control = await fixture.RegisterAsync(token);
        var controlId = control.GetProperty("id").GetString()!;
        await fixture.PutAsync("control", token);
        var ownedHere = await fixture.WaitForRequestAsync(token);
        using (var body = JsonDocument.Parse(ownedHere.Body))
        {
            await Assert.That(body.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(controlId);
        }

        _ = await fixture.WaitForDeliveryAsync(controlId, "delivered", token);
        var untouched = await fixture.WaitForDeliveryAsync(originalId, "pending", token, "http_error");
        await Assert.That(untouched.GetProperty("attempts").GetInt64()).IsEqualTo(1);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(2);
        await fixture.RestartAsync(token);
        var resumed = await fixture.WaitForRequestAsync(token);
        await Assert.That(resumed.Delivery).IsEqualTo(pending.Delivery);
        await Assert.That(resumed.Body.SequenceEqual(pending.Body)).IsTrue();
        await SignedAsync(resumed, original.GetProperty("secret").GetString()!);
        _ = await fixture.WaitForDeliveryAsync(originalId, "delivered", token);
    }

    internal static async Task SignedAsync(WebhookRequest request, string secret)
    {
        await Assert.That(request.Method).IsEqualTo("POST");
        await Assert.That(request.ContentType).IsEqualTo("application/json");
        await Assert.That(EntityIds.IsValid(request.Delivery)).IsTrue();
        var key = Base64Url.DecodeFromChars(secret);
        try
        {
            var prefix = Encoding.ASCII.GetBytes(request.Timestamp + ".");
            var signed = new byte[prefix.Length + request.Body.Length];
            prefix.CopyTo(signed, 0);
            request.Body.CopyTo(signed, prefix.Length);
            var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, signed));
            await Assert.That(request.Signature).IsEqualTo(expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
