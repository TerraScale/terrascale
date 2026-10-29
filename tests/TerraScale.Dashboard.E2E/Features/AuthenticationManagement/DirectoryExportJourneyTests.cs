using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DirectoryExportJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task AdminDownloadsOneSdkPageAtATimeAndResumesWithFreshAuthorization()
        {
            const string cursor = "opaque+/=%cursor";
            var producerErrors = new ConcurrentQueue<string>();
            var requests = 0;
            var reject = false;
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root, liveProjects: true, projectExtension: http =>
            {
                if (!http.Request.Path.Value!.EndsWith("/users/export", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                if (http.Request.Method != "GET" || http.Request.Query["limit"] != "100")
                {
                    producerErrors.Enqueue("The SDK must request one bounded GET page.");
                }

                requests++;
                if (reject)
                {
                    return Task.FromResult<IResult?>(Results.Json(new { error = "forbidden" }, statusCode: 403));
                }

                var after = http.Request.Query["after"].ToString();
                if (after.Length != 0 && after != cursor)
                {
                    producerErrors.Enqueue("The opaque cursor changed across the SDK boundary.");
                }

                return Task.FromResult<IResult?>(Results.Json(new
                {
                    items = after.Length == 0 ? new[] { new { id = "01JA0000000000000000000001", email = "=synthetic@example.com", email_verified = true,
                        created_at = "2026-10-01T00:00:00Z", disabled = false, linked_providers = new[] { new { provider = "oidc", client_id = "synthetic", subject = "public-subject" } } } } : [],
                    next = after.Length == 0 ? cursor : null,
                    created_before = "2026-10-25T00:00:00Z"
                }));
            });
            using var browser = scenario.CreateClient();
            using (var anonymous = await Export("json"))
            {
                Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Export requires sign-in");
            }

            using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "The owner signs in");
            }

            var html = await browser.GetStringAsync("/auth/users");
            var csrf = Field("_csrf");
            var project = Field("project_id");
            using (var refused = await Export("json", csrf: "wrong", project: project))
            {
                Check(refused.StatusCode == HttpStatusCode.Forbidden && requests == 0, "CSRF is enforced before the SDK");
            }

            using (var changed = await Export("json", csrf: csrf, project: "other"))
            {
                Check(changed.StatusCode == HttpStatusCode.Conflict && requests == 0, "A stale project form cannot export another project");
            }

            using (var invalid = await Export("xml", csrf: csrf, project: project))
            {
                Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity && requests == 0, "Unsupported formats do not call ts-auth");
            }

            using (var json = await Export("json", csrf: csrf, project: project))
            {
                Check(json.StatusCode == HttpStatusCode.OK && requests == 1 && json.Headers.CacheControl?.NoStore == true, "One click makes one uncached SDK call");
                using var page = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
                Check(page.RootElement.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[] { "created_before", "items", "next" }), "JSON page has only documented fields");
                Check(page.RootElement.GetProperty("items")[0].EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[] { "created_at", "disabled", "email", "email_verified", "id", "linked_providers" }), "JSON user has only allowlisted identity fields");
                Check(page.RootElement.GetProperty("next").GetString() == cursor && page.RootElement.GetProperty("items")[0].GetProperty("linked_providers")[0].GetProperty("subject").GetString() == "public-subject", "JSON preserves public linked identities and resume data");
                Check(json.Headers.GetValues("X-Export-Next").Single() == cursor && json.Headers.Contains("X-Export-Created-Before"), "Both formats expose resume headers");
            }
            using (var csv = await Export("csv", csrf: csrf, project: project))
            {
                Check(csv.StatusCode == HttpStatusCode.OK && csv.Content.Headers.ContentType?.MediaType == "text/csv" && (await csv.Content.ReadAsStringAsync()).Contains("\"'=synthetic@example.com\""), "CSV escapes formula-like emails");
            }

            reject = true;
            using (var revoked = await Export("json", cursor, csrf, project))
            {
                Check(revoked.StatusCode == HttpStatusCode.Forbidden && requests == 3, "The resumed SDK page checks authorization again");
            }

            reject = false;
            using (var last = await Export("json", cursor, csrf, project))
            {
                using var page = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
                Check(!last.Headers.Contains("X-Export-Next") && page.RootElement.GetProperty("next").ValueKind == JsonValueKind.Null && page.RootElement.GetProperty("items").GetArrayLength() == 0 && requests == 4, "The final empty page ends the scan");
            }
            using var producer = scenario.CreateProviderClient();
            using var control = await producer.PostAsJsonAsync("/__test/control", new { command = "workspace-role", role = "member" });
            using var member = await Export("csv", cursor, csrf, project);
            Check(member.StatusCode == HttpStatusCode.Forbidden && requests == 4, "Members cannot export even with an existing cursor");

            Check(producerErrors.IsEmpty, string.Join("; ", producerErrors));

            string Field(string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);
            Task<HttpResponseMessage> Export(string format, string after = "", string csrf = "", string project = "") =>
                HttpJourney.PostFormAsync(browser, "/api/v1/auth-management/users/export", new() { ["_csrf"] = csrf, ["project_id"] = project, ["format"] = format, ["after"] = after });
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
