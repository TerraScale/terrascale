using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BackupPolicyJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("unavailable")]
        [Arguments("missing")]
        [Arguments("invalid")]
        [Arguments("unsaved-zero")]
        [Arguments("deadline")]
        [Arguments("invalid-cron")]
        [Arguments("six-field-cron")]
        [Arguments("invalid-macro")]
        public async Task KnownSnapshotsRemainUsableWhenSchedulerPolicyCannotBeReported(string failure)
        {
            var failedPolicy = "";
            var cancelledPolicyRequests = 0;
            var policyArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, configureScenario: builder =>
            {
                var original = (IHttpClientFactory)builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new PolicyDeadlineFactory(original, () => Volatile.Read(ref failedPolicy) == "deadline", policyArrived.Task)));
            }, producerExtension: async http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.StartsWith("/v1/projects/", StringComparison.Ordinal) ||
                    !http.Request.Path.Value.EndsWith("/backup-policy", StringComparison.Ordinal) || Volatile.Read(ref failedPolicy).Length == 0)
                {
                    return null;
                }

                if (Volatile.Read(ref failedPolicy) == "deadline")
                {
                    policyArrived.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); }
                    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
                    { Interlocked.Increment(ref cancelledPolicyRequests); return Results.StatusCode(499); }
                    throw new InvalidOperationException("The stalled policy request must be cancelled.");
                }
                return Volatile.Read(ref failedPolicy) switch
                {
                    "unavailable" => Results.Json(new { error = "unavailable" }, statusCode: 503),
                    "missing" => Results.Json(new { error = "not_found" }, statusCode: 404),
                    "unsaved-zero" => Results.Json(new { fullBackupCron = "", retentionDays = 0, disabled = false, updatedAt = (DateTimeOffset?)null }),
                    "invalid-cron" => Results.Json(new { fullBackupCron = "99 2 * * *", retentionDays = 7, disabled = false, updatedAt = (DateTimeOffset?)null }),
                    "six-field-cron" => Results.Json(new { fullBackupCron = "0 0 2 * * *", retentionDays = 7, disabled = false, updatedAt = (DateTimeOffset?)null }),
                    "invalid-macro" => Results.Json(new { fullBackupCron = "@not_a_schedule", retentionDays = 7, disabled = false, updatedAt = (DateTimeOffset?)null }),
                    _ => Results.Json(new { fullBackupCron = "0 2 * * *", retentionDays = -1, disabled = false, updatedAt = (DateTimeOffset?)null })
                };
            });
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            using var producer = host.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            using (var admitted = await producer.PostAsync("/__test/control", JsonContent(new
            { command = "plan", organizationId = identity.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId })))
            {
                await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "policy-source", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var databaseId = (await BodyAsync(created)).GetProperty("id").GetString()!;
            using var snapshot = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId });
            await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var backupId = (await BodyAsync(snapshot)).GetProperty("id").GetString()!;
            var secret = await HttpJourney.CreatePatAsync(client, "Independent backup reads", account.Password);
            var path = "/api/v1/dashboard/backups?databaseId=" + databaseId;
            await Assert.That((await ReadAsync(replica, path)).GetProperty("policy").GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
            Volatile.Write(ref failedPolicy, failure);
            var knownRows = await ReadAsync(replica, path);
            await Assert.That(knownRows.GetProperty("backups")[0].GetProperty("id").GetString()).IsEqualTo(backupId);
            await Assert.That(knownRows.TryGetProperty("policy", out _)).IsFalse()
                .Because("Missing, invalid and unsaved-zero policy never becomes a usable scheduler setting.");
            var listed = await CallAsync(replica, secret, "list_backups", new { databaseId });
            await Assert.That(listed.GetProperty("backups").GetArrayLength()).IsEqualTo(1);
            await Assert.That(listed.GetProperty("policy").ValueKind).IsEqualTo(JsonValueKind.Null);
            var detail = await CallAsync(replica, secret, "get_database", new { databaseId });
            await Assert.That(detail.TryGetProperty("backupsEnabled", out _)).IsFalse();
            await Assert.That(detail.GetProperty("unavailable").GetString()).Contains("Backup scheduling");
            using (var page = await replica.GetAsync("/databases/" + databaseId + "/backups"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("restore-" + backupId);
                await Assert.That(html).Contains("Provider backup scheduling is unavailable.");
                await Assert.That(html.Contains("Restore points unavailable.", StringComparison.Ordinal)).IsFalse();
            }
            using (var page = await replica.GetAsync("/databases/" + databaseId))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Available snapshots");
            }
            var writes = (await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32();
            using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/policy", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["fullBackupCron"] = "", ["incrementalBackupCron"] = "", ["retentionDays"] = "0" }))
            {
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32()).IsEqualTo(writes);
            using var restored = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["backupId"] = backupId, ["name"] = "policy-restored" });
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created)
                .Because("New-target snapshot restore needs a completed snapshot, independently of scheduling policy.");
            var restoredId = (await BodyAsync(restored)).GetProperty("id").GetString()!;
            if (failure == "deadline")
            {
                await Assert.That(Volatile.Read(ref cancelledPolicyRequests)).IsGreaterThan(0)
                    .Because("The actual HTTP dependency deadline cancels its stalled producer request, independently of caller cancellation.");
            }

            Volatile.Write(ref failedPolicy, "");
            await Assert.That((await ReadAsync(replica, path)).GetProperty("policy").GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
            foreach (var (id, name) in new[] { (restoredId, "policy-restored"), (databaseId, "policy-source") })
            {
                using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
                { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }
        }

        private static System.Net.Http.Json.JsonContent JsonContent(object value)
        {
            return System.Net.Http.Json.JsonContent.Create(value);
        }

        private sealed class PolicyDeadlineFactory(IHttpClientFactory inner, Func<bool> deadline, Task arrived) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new(new PolicyDeadlineHandler(inner.CreateClient(name), deadline, arrived));
            }
        }
        private sealed class PolicyDeadlineHandler(HttpClient inner, Func<bool> deadline, Task arrived) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                if (!deadline() || request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/backup-policy", StringComparison.Ordinal))
                {
                    return await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                await arrived.WaitAsync(ct);
                // Expire only the stalled dependency after it reached the producer, independent of CPU load.
                timeout.Cancel();
                return await pending;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
        private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        { using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return value.RootElement.Clone(); }
        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await BodyAsync(response); }
        private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
        {
            var result = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent").Clone();
        }
    }
}
