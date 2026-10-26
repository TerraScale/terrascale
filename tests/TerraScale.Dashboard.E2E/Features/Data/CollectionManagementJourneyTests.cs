using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class CollectionManagementJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task DeclaredSchemaRoundTripPreservesRejectedDraftAndEnforcesManagementAndConfirmation()
    {
        JsonElement? active = null;
        var methods = new List<string>();
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/collections/items/schema", StringComparison.Ordinal)) return null;
            methods.Add(http.Request.Method);
            await Assert.That(http.Request.Headers.Authorization.ToString()).StartsWith("Bearer ");
            await Assert.That(http.Request.Headers["X-TerraScale-Region-Pin"].ToString()).IsEqualTo("gru");
            if (http.Request.Method == "PUT")
            {
                using var parsed = await JsonDocument.ParseAsync(http.Request.Body);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("unsupported", out _)) return Results.Json(new { error = new { code = "invalid_schema", message = "Unsupported schema keyword." } }, statusCode: 422);
                active = parsed.RootElement.Clone();
            }
            if (http.Request.Method == "DELETE") active = null;
            return Results.Json(new { schema = active, dialect = active is null ? null : "https://json-schema.org/draft/2020-12/schema" });
        });
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var path = $"/databases/{id}/schema?collection=items";
        await Assert.That(await client.GetStringAsync(path)).Contains("No declared validation schema.").And.Contains("Inferred fields");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["collection"] = "items", ["operation"] = "apply", ["schema"] = "{\"type\":\"object\",\"required\":[\"name\"]}" };
        using (var applied = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await client.GetStringAsync(path)).Contains("Active JSON Schema").And.Contains("required").And.Contains("historical documents");
        form["schema"] = "{ \"unsupported\": true }";
        using (var rejected = await HttpJourney.PostFormAsync(client, path, form))
        {
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync())).Contains(form["schema"]).And.Contains("Unsupported schema keyword.");
        }
        form["_csrf"] = "invalid";
        using (var csrfFailure = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(csrfFailure.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        form["_csrf"] = csrf;
        using (var foreign = await HttpJourney.PostFormAsync(client, "/databases/" + AccountService.NewId() + "/schema", form)) await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var controls = host.CreateProviderClient();
        using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "member" })) role.EnsureSuccessStatusCode();
        await Assert.That(await client.GetStringAsync(path)).DoesNotContain("Apply validation schema");
        using (var denied = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(methods.Count(method => method != "GET")).IsEqualTo(2);
        using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "admin" })) role.EnsureSuccessStatusCode();
        form["schema"] = "false";
        using (var applied = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        form["operation"] = "remove";
        using (var unconfirmed = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(unconfirmed.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        form["confirmName"] = "items";
        using (var removed = await HttpJourney.PostFormAsync(client, path, form)) await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await client.GetStringAsync(path)).Contains("No declared validation schema.");
        await Assert.That(methods.Count(method => method == "DELETE")).IsEqualTo(1);
    }

    [Test]
    public async Task MultipartUnicodeSchemaAppliesAndKeepsDraftsOnProducerAndDecodedSizeRefusals()
    {
        var writes = 0;
        JsonElement? active = null;
        var description = new string('界', 10000) + "</textarea><script>alert(1)</script>";
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/collections/items/schema", StringComparison.Ordinal)) return null;
            if (http.Request.Method == "GET") return Results.Json(new { schema = active });
            Interlocked.Increment(ref writes);
            using var wire = new MemoryStream();
            await http.Request.Body.CopyToAsync(wire);
            if (wire.Length > 64 * 1024) return Results.StatusCode(413);
            using var parsed = JsonDocument.Parse(wire.ToArray());
            if (parsed.RootElement.TryGetProperty("unsupported", out _)) return Results.Json(new { error = new { code = "invalid_schema", message = "Unsupported schema keyword." } }, statusCode: 422);
            active = parsed.RootElement.Clone();
            return Results.Json(new { schema = active });
        });
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var path = $"/databases/{id}/schema?collection=items";
        await Assert.That(await client.GetStringAsync(path)).Contains("enctype=\"multipart/form-data\"");
        async Task<HttpResponseMessage> SubmitAsync(string draft, bool chunked = false)
        {
            draft = draft.ReplaceLineEndings("\r\n");
            using var content = new MultipartFormDataContent();
            foreach (var field in new Dictionary<string, string> { ["_csrf"] = csrf, ["collection"] = "items", ["operation"] = "apply", ["schema"] = draft }) content.Add(new StringContent(field.Value), field.Key);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            if (chunked) request.Headers.TransferEncodingChunked = true;
            request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            return await client.SendAsync(request);
        }
        async Task<string> ReadDraftAsync()
        {
            var html = await client.GetStringAsync(path);
            await Assert.That(html).DoesNotContain("</textarea><script>");
            var match = Regex.Match(html, "<textarea[^>]*id=\"declared-schema\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            await Assert.That(match.Success).IsTrue();
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        using (var applied = await SubmitAsync("{\"description\":\"" + description + "\"}")) await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var loaded = await ReadDraftAsync();
        using (var parsed = JsonDocument.Parse(loaded)) await Assert.That(parsed.RootElement.GetProperty("description").GetString()).IsEqualTo(description);
        using (var resubmitted = await SubmitAsync(loaded)) await Assert.That(resubmitted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var compact = "{\"description\":\"" + new string('x', 65518) + "\"}";
        using (var applied = await SubmitAsync(" \r\n" + compact)) await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var loadedCompact = await ReadDraftAsync();
        await Assert.That(loadedCompact).IsEqualTo(compact).And.DoesNotContain("\n");
        using (var resubmitted = await SubmitAsync(loadedCompact)) await Assert.That(resubmitted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var enumeration = "{\"enum\":[" + string.Join(",", Enumerable.Repeat("\"xxxxxxxxxxxx\"", 4000)) + "]}";
        using (var applied = await SubmitAsync(enumeration)) await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var loadedEnumeration = await ReadDraftAsync();
        await Assert.That(loadedEnumeration).IsEqualTo(enumeration).And.DoesNotContain("\n");
        using (var resubmitted = await SubmitAsync(loadedEnumeration)) await Assert.That(resubmitted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var draft in new[] { "{\"description\":\"" + description + "\",\"unsupported\":true}", "{\"description\":\"" + new string('x', 65519) + "\"}" })
        {
            using var refused = await SubmitAsync(draft);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync())).Contains(draft).And.Contains(draft.Contains("unsupported", StringComparison.Ordinal) ? "Unsupported schema keyword." : "Enter a JSON Schema of 64 KiB or less after compact UTF-8 encoding.");
        }
        foreach (var chunked in new[] { false, true })
        {
            using var tooLarge = await SubmitAsync("{\"description\":\"" + new string('界', 70000) + "\"}", chunked);
            await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            await Assert.That(await tooLarge.Content.ReadAsStringAsync()).Contains("Schema submission is too large.").And.Contains("64 KiB");
        }
        await Assert.That(writes).IsEqualTo(7).Because("Only the initial schemas, unchanged resubmissions and producer-refused schema reach the SDK.");
    }

    [Test]
    public async Task IndexOverviewConfirmsDropsFiltersEmptyStateAndKeepsRejectedFieldDraft()
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["field"] = "name" };
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/indexes", form)) await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var html = await client.GetStringAsync($"/databases/{id}/indexes?collection=items");
        await Assert.That(html).Contains("items_name").And.Contains("active").And.Contains("commandfor=\"drop-index-0\"").And.Contains("may become slower or fail").And.DoesNotContain("Index templates");
        await Assert.That(await client.GetStringAsync($"/databases/{id}/indexes?collection=other")).Contains("No indexes on other.").And.DoesNotContain("items_name");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        form["field"] = "a,b,c,d,e";
        using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/indexes", form))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await invalid.Content.ReadAsStringAsync()).Contains("value=\"a,b,c,d,e\"");
        }
        form["name"] = "items_name";
        using (var dropped = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/indexes/delete", form))
        {
            await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(dropped.Headers.Location!.OriginalString).Contains("collection=items");
        }
        await Assert.That(await client.GetStringAsync($"/databases/{id}/indexes?collection=items")).Contains("No indexes on items.");
    }

    [Test]
    public async Task MultipartUnicodeDocumentFitsAndStaleDeleteShowsErrorInsideDialog()
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        const string documentId = "01K78D0C000000000000000099";
        var json = "{\"text\":\"" + new string('界', 133340) + "\"}";
        using var content = new MultipartFormDataContent();
        foreach (var field in new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "create", ["id"] = documentId, ["document"] = json }) content.Add(new StringContent(field.Value), field.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/documents") { Content = content };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using (var created = await client.SendAsync(request)) await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var oversized = new MultipartFormDataContent();
        foreach (var field in new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "replace", ["id"] = documentId, ["document"] = "{\"text\":\"" + new string('界', 349523) + "\"}" }) oversized.Add(new StringContent(field.Value), field.Key);
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/documents") { Content = oversized };
        oversizedRequest.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using (var refused = await client.SendAsync(oversizedRequest)) await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var html = await client.GetStringAsync($"/databases/{id}/documents/items?delete={documentId}");
        await Assert.That(html).Contains("enctype=\"multipart/form-data\"");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var stale = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "delete", ["id"] = documentId, ["version"] = "stale" });
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        html = await stale.Content.ReadAsStringAsync();
        var dialog = html[html.IndexOf("<dialog id=\"delete-document\"", StringComparison.Ordinal)..];
        await Assert.That(dialog).Contains("role=\"alert\"").And.Contains("The document changed. Reload it before saving again.");
        await Assert.That(await client.GetStringAsync($"/api/v1/dashboard/data/documents?databaseId={id}&collection=items")).Contains(documentId);
    }

    [Test]
    [Category("Slow")]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LargeDocumentEditResubmitsBrowserNormalizedDraftWithoutExpansionOrMarkup(int fixtureKind)
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http => Task.FromResult<IResult?>(
            http.Request.Method == "PUT" && http.Request.Path.Value!.EndsWith("/documents/by-key", StringComparison.Ordinal) && http.Request.ContentLength > 1024 * 1024 ? Results.StatusCode(413) : null));
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        const string documentId = "01K78D0C000000000000000198";
        var json = fixtureKind == 3 ? "{\"text\":\"" + new string('x', 1024 * 1024 - 11) + "\"}" : fixtureKind == 0 ? "{\"text\":\"" + new string('界', 200000) + "</textarea><script>alert(1)</script>\"}"
            : "{\"values" + (fixtureKind == 2 ? "\u0080" : "") + "\":[" + (fixtureKind == 2 ? "\"\u0080" + new string('x', 254) + "\"," : "")
                + string.Join(",", Enumerable.Repeat("\"" + new string('x', 255) + "\"", fixtureKind == 2 ? 3999 : 4000)) + "]}";
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(json) <= 1024 * 1024).IsTrue();
        async Task<HttpResponseMessage> SubmitAsync(string operation, string document, string version = "")
        {
            using var content = new MultipartFormDataContent();
            foreach (var field in new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = operation, ["id"] = documentId, ["version"] = version, ["document"] = document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal) }) content.Add(new StringContent(field.Value), field.Key);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/documents") { Content = content };
            request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            return await client.SendAsync(request);
        }
        using (var created = await SubmitAsync("create", fixtureKind == 3 ? " \r\n" + json : json)) await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await client.GetStringAsync($"/databases/{id}/documents/items?edit={documentId}");
        await Assert.That(html).DoesNotContain("</textarea><script>");
        var draft = await DraftAsync(html, "document-json");
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(draft) <= 1024 * 1024).IsTrue();
        var version = WebUtility.HtmlDecode(Regex.Match(html, "name=\"version\" value=\"([^\"]+)\"").Groups[1].Value);
        await Assert.That(version.Length).IsGreaterThan(0);
        using var expected = JsonDocument.Parse(json);
        using var displayed = JsonDocument.Parse(draft);
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, displayed.RootElement)).IsTrue();
        using (var replaced = await SubmitAsync("replace", draft, version)) await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var stored = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/dashboard/data/documents?databaseId={id}&collection=items"));
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, stored.RootElement.GetProperty("documents").EnumerateArray().Single().GetProperty("value"))).IsTrue();
    }

    [Test]
    [Category("Slow")]
    [Arguments('\u2028')]
    [Arguments('\u3000')]
    [Arguments('\u007f')]
    public async Task LargeDocumentDisplayDecodesExpandedProducerStringsWithoutChangingSemantics(char character)
    {
        using var expected = JsonDocument.Parse("{\"text\":\"" + new string(character, 200000) + "\"," + JsonSerializer.Serialize("key\"\\\0😀") + ":" + JsonSerializer.Serialize("quote\" backslash\\ literal\\u2028 pair😀 </textarea><script>alert(1)</script>" + string.Concat(Enumerable.Range(0, 32).Concat(Enumerable.Range(0x80, 32)).Select(value => (char)value))) + "}");
        const string documentId = "01K78D0C000000000000000198";
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http => Task.FromResult<IResult?>(
            http.Request.Method == "GET" && http.Request.Path.Value!.EndsWith("/documents/by-key", StringComparison.Ordinal)
                ? Results.Json(new { id = documentId, version = "tbv1-seeded", state = "value", document = expected.RootElement }) : null));
        using var client = host.CreateClient();
        var (id, _) = await SetupAsync(client);
        var html = await client.GetStringAsync($"/databases/{id}/documents/items?edit={documentId}");
        await Assert.That(html).DoesNotContain("</textarea><script>");
        var draft = await DraftAsync(html, "document-json");
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(draft) <= 1024 * 1024).IsTrue();
        using var displayed = JsonDocument.Parse(draft);
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, displayed.RootElement)).IsTrue();
    }

    [Test]
    [Category("Slow")]
    [Arguments('\u007f')]
    [Arguments('\u2028')]
    [Arguments('\u3000')]
    [Arguments('\u0080')]
    public async Task DeclaredSchemaRoundTripPreservesBlocklistedCharactersAndJsonStringSemantics(char character)
    {
        using var expected = JsonDocument.Parse("{\"description\":\"" + new string(character, 9000) + "\",\"$comment\":" + JsonSerializer.Serialize("quote\" backslash\\ literal\\u2028 pair😀 </textarea><script>alert(1)</script>" + string.Concat(Enumerable.Range(0, 32).Concat(Enumerable.Range(0x80, 32)).Select(value => (char)value))) + "}");
        var active = expected.RootElement.Clone();
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/collections/items/schema", StringComparison.Ordinal)) return null;
            if (http.Request.Method == "PUT")
            {
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body);
                if (body.Length > 64 * 1024) return Results.StatusCode(413);
                using var parsed = JsonDocument.Parse(body.ToArray());
                active = parsed.RootElement.Clone();
            }
            return Results.Json(new { schema = active });
        });
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var path = $"/databases/{id}/schema?collection=items";
        var html = await client.GetStringAsync(path);
        await Assert.That(html).DoesNotContain("</textarea><script>");
        var draft = await DraftAsync(html, "declared-schema");
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(draft) <= 64 * 1024).IsTrue();
        using var displayed = JsonDocument.Parse(draft);
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, displayed.RootElement)).IsTrue();
        using var content = new MultipartFormDataContent();
        foreach (var field in new Dictionary<string, string> { ["_csrf"] = csrf, ["collection"] = "items", ["operation"] = "apply", ["schema"] = draft }) content.Add(new StringContent(field.Value), field.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var applied = await client.SendAsync(request);
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, active)).IsTrue();
    }

    [Test]
    [Category("Slow")]
    [Arguments("create")]
    [Arguments("replace")]
    [Arguments("patch")]
    public async Task C1PresentationReachesSdkButRealDocumentOversizeDoesNot(string operation)
    {
        using var expected = JsonDocument.Parse("{\"text\":\"" + new string('\u0080', 200000) + "\"}");
        var writes = 0;
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/documents/by-key", StringComparison.Ordinal)) return null;
            if (http.Request.Method == "GET") return Results.Json(new { id = "01K78D0C000000000000000198", version = "tbv1-seeded", state = "value", document = expected.RootElement });
            Interlocked.Increment(ref writes);
            await Assert.That(http.Request.Headers.Authorization.ToString()).StartsWith("Bearer ");
            await Assert.That(operation == "create" ? http.Request.Headers.IfNoneMatch.ToString() : http.Request.Headers.IfMatch.ToString()).IsEqualTo(operation == "create" ? "*" : "\"tbv1-seeded\"");
            using var wire = new MemoryStream();
            await http.Request.Body.CopyToAsync(wire);
            await Assert.That(wire.Length).IsEqualTo(1200011L).Because("The installed SDK still expands C1; the producer admits at most 1 MiB.");
            using var value = JsonDocument.Parse(wire.ToArray());
            await Assert.That(JsonElement.DeepEquals(expected.RootElement, value.RootElement)).IsTrue();
            return Results.Json(new { error = new { code = "payload_too_large", message = "A document is at most 1 MiB." } }, statusCode: 413);
        });
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var draft = await DraftAsync(await client.GetStringAsync($"/databases/{id}/documents/items?edit=01K78D0C000000000000000198"), "document-json");
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(draft)).IsEqualTo(1200011);
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = operation, ["id"] = "01K78D0C000000000000000198", ["version"] = "tbv1-seeded", ["document"] = draft };
        foreach (var submitted in new[] { draft, expected.RootElement.GetRawText() })
        {
            form["document"] = submitted;
            using var refused = await MultipartAsync(client, "/api/v1/dashboard/data/documents", form);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            var refusedHtml = await refused.Content.ReadAsStringAsync();
            await Assert.That(WebUtility.HtmlDecode(refusedHtml)).Contains("The encoded document exceeds the data API's 1 MiB request limit.").And.Contains("Your draft has been kept.");
            using var retained = JsonDocument.Parse(await DraftAsync(refusedHtml, operation == "create" ? "new-document-json" : "document-json"));
            await Assert.That(JsonElement.DeepEquals(expected.RootElement, retained.RootElement)).IsTrue();
        }
        form["document"] = "{\"text\":\"" + new string('x', 1024 * 1024 - 10) + "\"}";
        using (var oversized = await MultipartAsync(client, "/api/v1/dashboard/data/documents", form)) await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(writes).IsEqualTo(2).Because("Only a logically valid compact document reaches the SDK, despite its larger display draft.");
    }

    [Test]
    [Category("Slow")]
    public async Task C1SchemaPresentationReachesSdkButRealSchemaOversizeDoesNot()
    {
        using var expected = JsonDocument.Parse("{\"description\":\"" + new string('\u0080', 32000) + "\"}");
        var writes = 0;
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/collections/items/schema", StringComparison.Ordinal)) return null;
            if (http.Request.Method == "GET") return Results.Json(new { schema = expected.RootElement });
            Interlocked.Increment(ref writes);
            using var wire = new MemoryStream();
            await http.Request.Body.CopyToAsync(wire);
            await Assert.That(wire.Length).IsEqualTo(192018L).Because("The installed SDK still expands C1 beyond the producer's 64 KiB admission.");
            using var value = JsonDocument.Parse(wire.ToArray());
            await Assert.That(JsonElement.DeepEquals(expected.RootElement, value.RootElement)).IsTrue();
            return Results.Json(new { error = new { code = "payload_too_large", message = "A collection schema is at most 64 KiB." } }, statusCode: 413);
        });
        using var client = host.CreateClient();
        var (id, csrf) = await SetupAsync(client);
        var path = $"/databases/{id}/schema?collection=items";
        var html = await client.GetStringAsync(path);
        await Assert.That(html).Contains("maxlength=\"196608\"").And.Contains("64 KiB of compact UTF-8 JSON");
        var draft = await DraftAsync(html, "declared-schema");
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(draft)).IsEqualTo(192018);
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["collection"] = "items", ["operation"] = "apply", ["schema"] = draft };
        foreach (var submitted in new[] { draft, expected.RootElement.GetRawText() })
        {
            form["schema"] = submitted;
            using var refused = await MultipartAsync(client, path, form);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            var refusedHtml = await refused.Content.ReadAsStringAsync();
            await Assert.That(WebUtility.HtmlDecode(refusedHtml)).Contains("The encoded schema exceeds the data API's 64 KiB request limit.").And.Contains("Your draft has been kept.");
            using var retained = JsonDocument.Parse(await DraftAsync(refusedHtml, "declared-schema"));
            await Assert.That(JsonElement.DeepEquals(expected.RootElement, retained.RootElement)).IsTrue();
        }
        form["schema"] = "{\"description\":\"" + new string('x', 65519) + "\"}";
        using (var oversized = await MultipartAsync(client, path, form)) await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(writes).IsEqualTo(2);
    }

    private static async Task<HttpResponseMessage> MultipartAsync(HttpClient client, string path, Dictionary<string, string> form)
    {
        using var content = new MultipartFormDataContent();
        foreach (var field in form) content.Add(new StringContent(field.Value), field.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return await client.SendAsync(request);
    }

    private static async Task<string> DraftAsync(string html, string id)
    {
        var textarea = Regex.Match(html, "<textarea[^>]*id=\"" + id + "\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        await Assert.That(textarea.Success).IsTrue();
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(textarea.Value);
        return (await page.Locator("textarea").InputValueAsync())
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static async Task<(string Id, string Csrf)> SetupAsync(HttpClient client)
    {
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "collection-management", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetString()!;
        using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" });
        await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return (id, csrf);
    }
}
