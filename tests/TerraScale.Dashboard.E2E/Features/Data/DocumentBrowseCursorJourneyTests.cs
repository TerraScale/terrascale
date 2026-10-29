using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DocumentBrowseCursorJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("")]
        [Arguments("q08tailneedle")]
        [Arguments("absent")]
        [Arguments("dense")]
        [Arguments("mixed")]
        [Arguments("large-cursor")]
        public async Task ContinuationsReachEveryDocumentWithoutRescanningEarlierPages(string search)
        {
            var large = search == "large-cursor";
            if (large)
            {
                search = "dense";
            }

            var count = large ? 52 : 1002;
            var track = true;
            var failReads = false;
            var writes = 0;
            var offsets = new List<int>();
            var limits = new List<int>();
            var returned = new List<int>();
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (http.Request.Path.Value!.EndsWith("/documents/by-key", StringComparison.Ordinal))
                {
                    var key = http.Request.Query["id"].ToString();
                    if (http.Request.Method == "GET")
                    {
                        return Results.Json(new { id = key, version = "v1", state = "value", document = new { text = "dense" } });
                    }

                    if (++writes == 1)
                    {
                        return Results.StatusCode(412);
                    }

                    return Results.Json(new { id = key, version = "v2", offset = 1L, durability = "local_durable" });
                }
                if (!http.Request.Path.Value!.EndsWith("/query", StringComparison.Ordinal))
                {
                    return null;
                }

                if (failReads)
                {
                    return Results.StatusCode(503);
                }

                using var request = await JsonDocument.ParseAsync(http.Request.Body);
                var query = request.RootElement;
                var cursor = query.TryGetProperty("cursor", out var value) ? value.GetString() : null;
                var offset = string.IsNullOrEmpty(cursor) ? 0 : int.Parse(cursor.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                var limit = query.GetProperty("limit").GetInt32();
                if (track) { offsets.Add(offset); limits.Add(limit); }
                await Assert.That(query.GetProperty("sort")[0].GetProperty("path").GetString()).IsEqualTo("/a~1b~0c");
                await Assert.That(query.GetProperty("sort")[0].GetProperty("order").GetString()).IsEqualTo("desc");
                var rows = Enumerable.Range(offset, Math.Min(limit, count - offset)).Select(index => new
                {
                    id = $"01K78D0C00000000000000{index:D4}",
                    version = "v1",
                    state = "value",
                    document = new { text = "dense" + (index == 1001 ? " q08tailneedle mixed" : index < 24 ? " mixed" : "") }
                }).ToArray();
                if (track)
                {
                    returned.Add(rows.Length);
                }

                var next = $"scan:{offset + rows.Length}:opaque/+";
                return Results.Json(new { items = rows, next_cursor = offset + rows.Length < count ? large ? next.PadRight(1024 * 1024, '+') : next : null });
            });
            using var client = host.CreateClient();
            var (id, password) = await SetupAsync(client);
            var path = $"/databases/{id}/documents/items?view=list&sort=a%2Fb~c&order=desc&q={search}";
            var displayed = new List<string>();
            var scans = 0;
            Dictionary<string, string>? fields = null;
            while (true)
            {
                var before = limits.Count;
                using var response = fields is null ? await client.GetAsync(path) : await HttpJourney.PostFormAsync(client, path, fields);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                if (large && scans == 1)
                {
                    track = false;
                    await Assert.That(Regex.Matches(html, "(?:href|formaction)=\"([^\"]+)\"").All(match => match.Groups[1].Value.Length < 8192)).IsTrue();
                    var context = Hidden(Regex.Match(html, "<form id=\"document-browse-context\".*?</form>", RegexOptions.Singleline).Value);
                    await Assert.That(context["cursor"].Length).IsEqualTo(1024 * 1024);
                    using (var refused = await HttpJourney.PostFormAsync(client, path, new(context) { ["_csrf"] = "invalid" }))
                    {
                        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                    }

                    foreach (var action in new[] { "Grid", "List", "edit", "delete" })
                    {
                        var target = Regex.Match(html, action is "Grid" or "List" ? "formaction=\"([^\"]+)\"[^>]*aria-label=\"" + action + " view\"" : "formaction=\"([^\"]*[?&]" + action + "=[^\"]+)\"").Groups[1].Value;
                        using var navigated = await HttpJourney.PostFormAsync(client, target, context);
                        await Assert.That(navigated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        var expected = action == "Grid" ? "ts-data-grid" : action == "List" ? "ts-data-documents" : "id=\"" + action + "-document\"";
                        await Assert.That((await navigated.Content.ReadAsStringAsync()).Contains(expected, StringComparison.Ordinal)).IsTrue().Because(action + " must retain the browse context.");
                    }
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    var draft = new Dictionary<string, string>(context) { ["databaseId"] = id, ["collection"] = "items", ["page"] = "2", ["view"] = "list", ["sort"] = "a/b~c", ["order"] = "desc", ["q"] = search, ["id"] = "01K78D0C000000000000000025", ["operation"] = "replace", ["version"] = "stale", ["document"] = "{}" };
                    using (var invalidCollection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new(draft) { ["name"] = "Invalid" }))
                    {
                        await Assert.That(invalidCollection.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                        var collectionRetry = Hidden(Regex.Match(WebUtility.HtmlDecode(await invalidCollection.Content.ReadAsStringAsync()), "<dialog id=\"new-collection\".*?</dialog>", RegexOptions.Singleline).Value);
                        await Assert.That(collectionRetry["cursor"]).IsEqualTo(context["cursor"]);
                        await Assert.That(collectionRetry["page"]).IsEqualTo("2");
                    }
                    using (var refusedDelete = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections/delete", new(draft) { ["confirmName"] = "wrong" }))
                    {
                        await Assert.That(refusedDelete.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                        var retained = Hidden(Regex.Match(WebUtility.HtmlDecode(await refusedDelete.Content.ReadAsStringAsync()), "<dialog id=\"delete-collection\".*?</dialog>", RegexOptions.Singleline).Value);
                        await Assert.That(retained["cursor"]).IsEqualTo(context["cursor"]);
                        await Assert.That(retained["page"]).IsEqualTo("2");
                    }
                    draft["document"] = "{\"text\":\"" + new string('d', 1024 * 1024 - 11) + "\"}";
                    failReads = true;
                    using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", draft);
                    await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
                    var retry = Hidden(Regex.Match(WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync()), "<dialog id=\"edit-document\".*?</dialog>", RegexOptions.Singleline).Value);
                    await Assert.That(retry["cursor"] == context["cursor"]).IsTrue().Because("The complete cursor must survive the failed browse read.");
                    await Assert.That(retry["page"]).IsEqualTo("2");
                    failReads = false;
                    draft["version"] = "v1";
                    using var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", draft);
                    await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
                    var targetPath = saved.Headers.Location!.OriginalString;
                    await Assert.That(targetPath).StartsWith($"/databases/{id}/documents/items").And.DoesNotContain("cursor=");
                    for (var refresh = 0; refresh < 2; refresh++)
                    {
                        using var refreshed = await HttpJourney.PostFormAsync(client, targetPath, draft);
                        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    }
                    await Assert.That(writes).IsEqualTo(2);
                    using var controls = host.CreateProviderClient();
                    using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" }))
                    {
                        role.EnsureSuccessStatusCode();
                    }

                    using (var viewed = await HttpJourney.PostFormAsync(client, Regex.Match(html, "formaction=\"([^\"]*[?&]edit=[^\"]+)\"").Groups[1].Value, context))
                    {
                        var readOnly = await viewed.Content.ReadAsStringAsync();
                        await Assert.That(readOnly.Contains("<span>View</span>", StringComparison.Ordinal) && !readOnly.Contains("Save document", StringComparison.Ordinal)).IsTrue();
                    }
                    using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "admin" }))
                    {
                        role.EnsureSuccessStatusCode();
                    }

                    var credential = await HttpJourney.CreatePatAsync(client, "Cursor transport", password);
                    var rpc = await HttpJourney.McpAsync(client, credential, "tools/call", new
                    {
                        name = "list_documents",
                        arguments = new { databaseId = id, collection = "items", sort = "a/b~c", descending = true, limit = 1, after = context["cursor"] }
                    });
                    var output = rpc.GetProperty("result").GetProperty("structuredContent");
                    await Assert.That(output.GetProperty("documents").GetArrayLength()).IsEqualTo(1);
                    await Assert.That(output.GetProperty("limit").GetInt32()).IsEqualTo(1);
                    await Assert.That(output.GetProperty("hasMore").GetBoolean()).IsTrue();
                    await Assert.That(output.GetProperty("nextCursor").GetString()!.Length).IsEqualTo(1024 * 1024);
                    track = true;
                }
                var rows = Regex.Matches(html, "class=\"(?:ts-btn ts-btn-ghost )?ts-data-doc-id\"[^>]*>([^<]+)</(?:a|button)>").Select(match => match.Groups[1].Value).ToArray();
                displayed.AddRange(rows);
                await Assert.That(rows.Length <= 25).IsTrue();
                await Assert.That(limits.Count - before <= (search.Length == 0 ? 1 : 4)).IsTrue();
                await Assert.That(limits.Skip(before).Sum() <= 100).IsTrue();
                await Assert.That(html).DoesNotContain("first 1,000");
                var next = Regex.Match(html, "<form id=\"document-browse-next\"[^>]*action=\"([^\"]+)\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
                if (!next.Success)
                {
                    await Assert.That(html).Contains("End of collection reached.");
                    if (search == "absent")
                    {
                        await Assert.That(html).Contains("No more matches.");
                    }

                    break;
                }
                if (search is "q08tailneedle" or "absent")
                {
                    await Assert.That(html).Contains("No matches in this scan.").And.Contains("Partial search.");
                }

                path = next.Groups[1].Value;
                fields = Regex.Matches(next.Groups[2].Value, "type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"").ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
                await Assert.That(fields.ContainsKey("cursor")).IsTrue();
                await Assert.That(path).Contains("view=list").And.Contains("sort=a%2Fb~c").And.Contains("order=desc").And.DoesNotContain("cursor=");
                await Assert.That(++scans <= 41).IsTrue();
            }
            await Assert.That(displayed.Count).IsEqualTo(search == "absent" ? 0 : search == "q08tailneedle" ? 1 : search == "mixed" ? 25 : count);
            await Assert.That(displayed.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(displayed.Count);
            await Assert.That(offsets.SequenceEqual(Enumerable.Range(0, offsets.Count).Select(index => returned.Take(index).Sum()))).IsTrue();
            await Assert.That(returned.Sum()).IsEqualTo(count);
            await Assert.That(limits.All(limit => limit is > 0 and <= 25)).IsTrue();
        }

        private static Dictionary<string, string> Hidden(string html)
        {
            return Regex.Matches(html, "type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        }

        [Test]
        [Arguments("empty")]
        [Arguments("failure")]
        [Arguments("repeat")]
        public async Task EmptyCollectionsAndFailedOrStalledReadsHaveTruthfulMessages(string outcome)
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
                Task.FromResult<IResult?>(!http.Request.Path.Value!.EndsWith("/query", StringComparison.Ordinal) ? null
                    : outcome == "failure" ? Results.StatusCode(503)
                    : Results.Json(new { items = Array.Empty<object>(), next_cursor = outcome == "repeat" ? "repeat" : null })));
            using var client = host.CreateClient();
            var (id, _) = await SetupAsync(client);
            var html = await client.GetStringAsync($"/databases/{id}/documents/items" + (outcome == "repeat" ? "?q=needle" : ""));
            if (outcome == "empty")
            {
                await Assert.That(html).Contains("This collection has no documents yet.").And.Contains("End of collection reached.");
            }
            else
            {
                await Assert.That(html).Contains("Documents could not be loaded.").And.Contains("Document results unavailable.").And.DoesNotContain("End of collection reached.");
            }
        }

        private static async Task<(string Id, string Password)> SetupAsync(HttpClient client)
        {
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "cursor-browse", ["region"] = "GRU" });
            created.EnsureSuccessStatusCode();
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = database.RootElement.GetProperty("id").GetString()!;
            using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" });
            collection.EnsureSuccessStatusCode();
            return (id, account.Password);
        }
    }
}
