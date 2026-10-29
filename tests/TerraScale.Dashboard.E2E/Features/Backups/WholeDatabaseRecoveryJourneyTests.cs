using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Backups;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class WholeDatabaseRecoveryJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task HttpAndMcpRestoreNewDatabasesWithSafeErrorsAndSourceUnchanged()
        {
            var earliest = DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture);
            var latest = earliest.AddMinutes(10);
            var noHistory = true;
            var failure = "";
            var snapshotId = "";
            var calls = new List<JsonElement>();
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (!http.Request.Path.Value!.Contains("/point-in-time", StringComparison.Ordinal))
                {
                    return null;
                }

                await Assert.That(http.Request.Path.Value).Contains("/databases/pitr-source/point-in-time");
                await Assert.That(http.Request.Host.Host).IsEqualTo("gru.api.terrascale.tech");
                await Assert.That(http.Request.Headers["X-TerraScale-Region-Pin"].ToString()).IsEqualTo("gru");
                if (http.Request.Method == "GET")
                {
                    return failure == "window-unavailable" ? Results.StatusCode(503) : noHistory ? Results.NotFound() : Results.Json(new { earliest, latest });
                }

                using var body = await JsonDocument.ParseAsync(http.Request.Body);
                calls.Add(body.RootElement.Clone());
                await Assert.That(body.RootElement.GetProperty("destination").GetString()).IsEqualTo("customer");
                if (failure.Length > 0)
                {
                    return Results.Json(new { error = new { code = failure, message = "private-provider-detail" } }, statusCode: failure == "invalid_request" ? 400 : failure == "unavailable" ? 503 : 409);
                }

                if (http.Request.Path.Value.EndsWith("/plan", StringComparison.Ordinal))
                {
                    return Results.Json(new { window = new { earliest, latest }, pointInTime = body.RootElement.GetProperty("pointInTime").GetDateTimeOffset(), snapshotAt = earliest, latestSegmentAt = latest, segmentCount = 2, historyCheck = "metadata" });
                }
                // Reuse the producer's stored historical snapshot copy and managed readback, rather than inventing success metadata.
                http.Request.Path = http.Request.Path.Value.Replace("/point-in-time/restore", "/backups/" + snapshotId + "/restore", StringComparison.Ordinal);
                http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name = body.RootElement.GetProperty("name").GetString() })));
                return null;
            });
            using var client = host.CreateClient();
            using var producer = host.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "PITR contract journey", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var source = await CallAsync("create_database", new { name = "pitr-source", region = "GRU" });
            var id = source.GetProperty("id").GetString()!;
            var path = "/databases/" + id + "/point-in-time";
            using (var empty = await client.GetAsync(path))
            {
                await Assert.That(await empty.Content.ReadAsStringAsync()).Contains("No retained PITR history");
            }

            var inventory = await CallAsync("list_backups", new { databaseId = id });
            await Assert.That(inventory.GetProperty("backups").GetArrayLength()).IsEqualTo(0);
            noHistory = false;
            var window = await CallAsync("get_point_in_time_window", new { databaseId = id, destination = "customer" });
            await Assert.That(window.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(latest);
            using (var page = await client.GetAsync(path + "?destination=customer"))
            {
                var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
                await Assert.That(html).Contains("value=\"" + RecoveryWindow.Input(latest) + "\"").And.Contains("min=\"" + RecoveryWindow.Input(earliest) + "\"").And.Contains("one additional database slot");
                await Assert.That(html).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"").And.Contains(DatabaseService.NameHint);
            }
            var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["destination"] = "customer", ["point"] = RecoveryWindow.Input(latest), ["name"] = "pitr-copy", ["operation"] = "plan" };
            using (var plan = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(await plan.Content.ReadAsStringAsync()).Contains("metadata only").And.Contains("does not verify replay bytes").And.Contains("Type pitr-copy");
            }

            foreach (var code in new[] { "backup_history_incomplete", "restore_point_unproven", "backup_unreadable", "database_limit", "invalid_request", "unavailable" })
            {
                var callsBeforeFailure = calls.Count;
                failure = code;
                using var refused = await HttpJourney.PostFormAsync(client, path, form);
                await Assert.That((int)refused.StatusCode).IsGreaterThanOrEqualTo(400);
                await Assert.That(await refused.Content.ReadAsStringAsync()).Contains(code switch { "backup_history_incomplete" => "history is incomplete", "restore_point_unproven" => "cannot prove", "backup_unreadable" => "could not be verified", "database_limit" => "one free database slot", "invalid_request" => "outside retained history", _ => "temporarily unavailable" }).And.Contains("value=\"pitr-copy\"").And.Contains("value=\"" + form["point"] + "\"").And.DoesNotContain("private-provider-detail");
                var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "plan_point_in_time_recovery", arguments = new { databaseId = id, pointInTime = latest.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), destination = "customer" } });
                await Assert.That(rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
                await Assert.That(calls.Count).IsEqualTo(callsBeforeFailure + 2);
                await Assert.That(rpc.GetRawText()).DoesNotContain("private-provider-detail").And.DoesNotContain("An error occurred invoking");
            }
            failure = "window-unavailable";
            using (var outage = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(await outage.Content.ReadAsStringAsync()).Contains("value=\"pitr-copy\"").And.Contains("value=\"" + form["point"] + "\"");
            }

            failure = "";
            var before = calls.Count;
            form["point"] = RecoveryWindow.Input(latest.AddSeconds(1));
            using (var bounds = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(bounds.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            form["point"] = RecoveryWindow.Input(earliest.AddSeconds(-1));
            using (var bounds = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(bounds.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            using (var invalid = await client.GetAsync(path + "?destination=other"))
            {
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            form["point"] = RecoveryWindow.Input(latest);
            form["operation"] = "restore";
            form["confirmName"] = "pitr-copy";
            using (var quota = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(quota.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            using var identity = await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/me");
            // Fixture-only paid-plan admission, matching the existing backup journey; production quota is unchanged.
            using var planControl = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId = identity!.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            form["name"] = form["confirmName"] = "pitr-source";
            using (var overwrite = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(overwrite.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            form["name"] = "pitr-copy";
            form["confirmName"] = "wrong";
            using (var confirm = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            form["confirmName"] = "pitr-copy";
            form["_csrf"] = "invalid";
            using (var csrfFailure = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(csrfFailure.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            await Assert.That(calls.Count).IsEqualTo(before);
            form["_csrf"] = csrf;
            await CallAsync("create_collection", new { databaseId = id, name = "users" });
            const string record = "01K78D0C000000000000000010";
            await CallAsync("create_document", new { databaseId = id, collection = "users", documentId = record, document = new { score = 1 } });
            var backup = await CallAsync("create_backup", new { databaseId = id });
            snapshotId = backup.GetProperty("id").GetString()!;
            await CallAsync("replace_document", new { databaseId = id, collection = "users", id = record, value = new { score = 2 } });
            using (var restored = await HttpJourney.PostFormAsync(client, path, form))
            {
                await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var second = await CallAsync("restore_database_point_in_time", new { databaseId = id, pointInTime = form["point"], name = "pitr-copy-two", confirmName = "pitr-copy-two", destination = "customer" });
            var current = await CallAsync("get_document", new { databaseId = id, collection = "users", documentId = record });
            var historical = await CallAsync("get_document", new { databaseId = second.GetProperty("databaseId").GetString(), collection = "users", documentId = record });
            await Assert.That(current.GetProperty("value").GetProperty("score").GetInt32()).IsEqualTo(2);
            await Assert.That(historical.GetProperty("value").GetProperty("score").GetInt32()).IsEqualTo(1);
            using var role = await producer.PostAsJsonAsync("/__test/control", new { command = "role", value = "member" });
            using (var denied = await client.GetAsync(path))
            {
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            using (var foreign = await client.GetAsync("/databases/" + AccountService.NewId() + "/point-in-time"))
            {
                await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            async Task<JsonElement> CallAsync(string name, object arguments)
            {
                var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
                await Assert.That(rpc.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name);
                return rpc.GetProperty("result").GetProperty("structuredContent");
            }
        }
    }
}
