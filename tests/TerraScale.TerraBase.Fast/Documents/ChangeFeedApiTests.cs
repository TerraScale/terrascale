using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class ChangeFeedApiTests
{
    private static readonly string?[] Operations = ["upsert", "upsert", "delete"];
    [Test]
    public async Task Repeated_writes_and_a_delete_in_one_transaction_paginate_without_loss()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var name = new Faker().Name.FirstName();
        var id = DocumentApi.NewId();
        var start = await ReadAsync(api, feed);
        // Client transactions reject duplicate targets; the raw engine history can still contain repeated writes.
        // Set up that committed history through the real server, then verify it only through the feed listener.
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>()
            .OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        long offset;
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var revision = 1; revision <= 3; revision++)
            {
                await write.PutAsync("items", id, JsonSerializer.SerializeToUtf8Bytes(new { name, revision }), CancellationToken.None);
            }

            await write.TombstoneAsync("items", id, CancellationToken.None);
            offset = await write.CommitAsync(CancellationToken.None);
        }

        var token = Token(start);
        long previousSequence = -1;
        for (var revision = 1; revision <= 4; revision++)
        {
            var page = await ReadAsync(api, feed, token, limit: 1);
            var changes = Changes(page);
            await Assert.That(changes.Length).IsEqualTo(1);
            var change = changes[0];
            await Assert.That(change.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(Number(change, "offset")).IsEqualTo(offset);
            await Assert.That(change.GetProperty("sequence").ValueKind).IsEqualTo(JsonValueKind.String);
            await Assert.That(Number(change, "sequence")).IsGreaterThan(previousSequence);
            await Assert.That(change.GetProperty("operation").GetString()).IsEqualTo(revision == 4 ? "delete" : "upsert");
            if (revision == 4)
            {
                await Assert.That(change.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
            else
            {
                await Assert.That(change.GetProperty("document").GetProperty("revision").GetInt32()).IsEqualTo(revision);
                await Assert.That(change.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            }

            await Assert.That(HasMore(page)).IsEqualTo(revision < 4);
            await Assert.That(Token(page)).IsNotEqualTo(token);
            previousSequence = Number(change, "sequence");
            token = Token(page);
        }

        var empty = await ReadAsync(api, feed, token, limit: 1);
        await Assert.That(Changes(empty)).IsEmpty();
        await Assert.That(Token(empty)).IsEqualTo(token);
        await Assert.That(HasMore(empty)).IsFalse();
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Inserts_updates_and_deletes_keep_each_committed_document_image()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var name = new Faker().Commerce.ProductName();
        var id = DocumentApi.NewId();
        var inserted = await api.PutAsync(id, new { name, revision = 1 });
        inserted.Status.ShouldBe(HttpStatusCode.Created);
        var updated = await api.PutAsync(id, new { name, revision = 2 });
        updated.Status.ShouldBe(HttpStatusCode.OK);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(id));
        deleted.Status.ShouldBe(HttpStatusCode.OK);

        // Omitting the token includes retained writes, rather than starting at the current head.
        var page = await ReadAsync(api, feed);
        var changes = Changes(page);
        await Assert.That(changes.Length).IsEqualTo(3);
        string?[] expectedIds = [id, id, id];
        await Assert.That(changes.Select(change => change.GetProperty("id").GetString()).SequenceEqual(expectedIds)).IsTrue();
        await Assert.That(changes.Select(change => change.GetProperty("operation").GetString()).SequenceEqual(Operations)).IsTrue();
        long[] expectedOffsets = [Number(inserted.Json, "offset"), Number(updated.Json, "offset"), Number(deleted.Json, "offset")];
        await Assert.That(changes.Select(change => Number(change, "offset")).SequenceEqual(expectedOffsets)).IsTrue();
        await Assert.That(changes[0].GetProperty("document").GetProperty("revision").GetInt32()).IsEqualTo(1);
        await Assert.That(changes[1].GetProperty("document").GetProperty("revision").GetInt32()).IsEqualTo(2);
        await Assert.That(changes[1].GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(changes[2].GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(HasMore(page)).IsFalse();
        await Assert.That(Token(page)).IsNotEmpty();
    }

    [Test]
    public async Task Pagination_orders_one_transaction_by_append_sequence_and_excludes_other_collections()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var faker = new Faker();
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).ToArray();
        var transaction = await api.TransactAsync(
            new { op = "create", collection = "items", id = ids[0], document = new { name = faker.Name.FirstName() } },
            new { op = "create", collection = "other", id = DocumentApi.NewId(), document = new { name = faker.Name.FirstName() } },
            new { op = "create", collection = "items", id = ids[1], document = new { name = faker.Name.FirstName() } },
            new { op = "create", collection = "items", id = ids[2], document = new { name = faker.Name.FirstName() } });
        transaction.Status.ShouldBe(HttpStatusCode.OK);

        string? token = null;
        long sequence = -1;
        for (var index = 0; index < ids.Length; index++)
        {
            var page = await ReadAsync(api, feed, token, limit: 1);
            var changes = Changes(page);
            await Assert.That(changes.Length).IsEqualTo(1);
            await Assert.That(changes[0].GetProperty("id").GetString()).IsEqualTo(ids[index]);
            await Assert.That(Number(changes[0], "offset")).IsEqualTo(Number(transaction.Json, "offset"));
            await Assert.That(Number(changes[0], "sequence")).IsGreaterThan(sequence);
            await Assert.That(HasMore(page)).IsEqualTo(index < ids.Length - 1);
            sequence = Number(changes[0], "sequence");
            token = Token(page);
        }

        await Assert.That(Changes(await ReadAsync(api, feed, token))).IsEmpty();
    }

    [Test]
    public async Task A_rejected_transaction_emits_no_events_and_the_next_commit_is_visible()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var name = new Faker().Name.FirstName();
        var before = await api.PutAsync(DocumentApi.NewId(), new { name });
        before.Status.ShouldBe(HttpStatusCode.Created);
        var start = await ReadAsync(api, feed);
        var rejectedId = DocumentApi.NewId();
        var rejected = await api.TransactAsync(
            new { op = "create", collection = "items", id = rejectedId, document = new { name } },
            new { op = "assert_count", collection = "items", lte = 0 });
        await Assert.That((rejected.Status, rejected.Code, rejected.Op))
            .IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", (int?)1));
        var empty = await ReadAsync(api, feed, Token(start));
        await Assert.That(Changes(empty)).IsEmpty();
        await Assert.That(Token(empty)).IsEqualTo(Token(start));

        var committedId = DocumentApi.NewId();
        var committed = await api.PutAsync(committedId, new { name });
        committed.Status.ShouldBe(HttpStatusCode.Created);
        var page = await ReadAsync(api, feed, Token(empty));
        var changes = Changes(page);
        await Assert.That(changes.Length).IsEqualTo(1);
        await Assert.That(changes[0].GetProperty("id").GetString()).IsEqualTo(committedId);
        await Assert.That(Number(changes[0], "offset")).IsEqualTo(Number(before.Json, "offset") + 1);
        await Assert.That((await api.GetAsync(rejectedId)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Empty_and_completed_pages_return_tokens_that_advance_to_the_database_head()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var start = await ReadAsync(api, feed);
        await Assert.That(Changes(start)).IsEmpty();
        await Assert.That(Token(start)).IsNotEmpty();
        await Assert.That(HasMore(start)).IsFalse();
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var otherDocuments = $"{api.Collections}/other/documents";
        (await api.SendAsync(HttpMethod.Put, $"{otherDocuments}/{DocumentApi.NewId()}", new { name = new Faker().Name.FirstName() }))
            .Status.ShouldBe(HttpStatusCode.Created);
        var empty = await ReadAsync(api, feed, Token(start));
        await Assert.That(Changes(empty)).IsEmpty();
        await Assert.That(Token(empty)).IsNotEqualTo(Token(start));
        await Assert.That(HasMore(empty)).IsFalse();

        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { name = new Faker().Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
        var completed = await ReadAsync(api, feed, Token(empty));
        (await api.SendAsync(HttpMethod.Put, $"{otherDocuments}/{DocumentApi.NewId()}", new { name = new Faker().Name.FirstName() }))
            .Status.ShouldBe(HttpStatusCode.Created);
        var atHead = await ReadAsync(api, feed, Token(empty), limit: 1);
        await Assert.That(Changes(atHead).Length).IsEqualTo(1);
        await Assert.That(Changes(atHead)[0].GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(HasMore(atHead)).IsFalse();
        await Assert.That(Token(atHead)).IsNotEqualTo(Token(completed));
        var resumed = await ReadAsync(api, feed, Token(atHead));
        await Assert.That(Changes(resumed)).IsEmpty();
        await Assert.That(Token(resumed)).IsEqualTo(Token(atHead));
    }

    [Test]
    public async Task Tokens_cannot_cross_collections_or_databases()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var token = Token(await ReadAsync(api, feed));
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("other")}/collections", new { name = "items" }))
            .Status.ShouldBe(HttpStatusCode.Created);

        foreach (var route in new[] { Route(api, collection: "other"), Route(api, database: "other") })
        {
            var rejected = await DocumentApi.SendAsync(feed, HttpMethod.Get, $"{route}?resume_token={Uri.EscapeDataString(token)}");
            await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.Conflict, "resume_expired"));
        }

        await Assert.That(Token(await ReadAsync(api, feed, token))).IsNotEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dropping_and_recreating_a_collection_or_database_invalidates_its_lineage(bool database)
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var name = new Faker().Name.FirstName();
        var oldId = DocumentApi.NewId();
        (await api.PutAsync(oldId, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        var token = Token(await ReadAsync(api, feed));
        if (database)
        {
            await api.DeleteDatabaseAsync();
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath())).Status.ShouldBe(HttpStatusCode.Created);
        }
        else
        {
            (await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/items")).Status.ShouldBe(HttpStatusCode.OK);
            var missing = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api));
            await Assert.That((missing.Status, missing.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        }

        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "items" })).Status.ShouldBe(HttpStatusCode.Created);
        var newId = DocumentApi.NewId();
        (await api.PutAsync(newId, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        var expired = await DocumentApi.SendAsync(feed, HttpMethod.Get, $"{Route(api)}?resume_token={Uri.EscapeDataString(token)}");
        await Assert.That((expired.Status, expired.Code)).IsEqualTo((HttpStatusCode.Conflict, "resume_expired"));
        var page = await ReadAsync(api, feed);
        await Assert.That(Changes(page).Length).IsEqualTo(1);
        await Assert.That(Changes(page)[0].GetProperty("id").GetString()).IsEqualTo(newId);
        await Assert.That(Token(page)).IsNotEqualTo(token);
    }

    [Test]
    public async Task Malformed_oversized_and_noncanonical_tokens_are_bad_requests()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var token = Token(await ReadAsync(api, feed));
        // Mutate an actual first-slice token, keeping its lineage valid where possible.
        var parts = token.Split('.');
        var invalid = new[]
        {
            "not-a-token", "alpha", new string('x', 129), token + ".extra",
            $"{parts[0]}.{parts[1]}.0{parts[2]}.{parts[3]}",
            $"{parts[0]}.{parts[1]}.{parts[2]}.0{parts[3]}",
            $"{parts[0]}.{parts[1]}.-1.{parts[3]}",
            $"{parts[0]}.{parts[1]}.9223372036854775808.{parts[3]}",
            $"{parts[0]}.{new string('A', 64)}.{parts[2]}.{parts[3]}",
        };
        foreach (var malformed in invalid)
        {
            var response = await DocumentApi.SendAsync(feed, HttpMethod.Get, $"{Route(api)}?resume_token={Uri.EscapeDataString(malformed)}");
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_resume_token"));
        }

        await Assert.That(Token(await ReadAsync(api, feed, token))).IsEqualTo(token);
    }

    [Test]
    public async Task Well_formed_tokens_for_a_future_offset_or_wrong_lineage_have_expired()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var parts = Token(await ReadAsync(api, feed)).Split('.');
        var wrongLineage = (parts[1][0] == '0' ? "1" : "0") + parts[1][1..];
        foreach (var token in new[]
        {
            $"{parts[0]}.{parts[1]}.{long.MaxValue.ToString(CultureInfo.InvariantCulture)}.{parts[3]}",
            $"{parts[0]}.{wrongLineage}.{parts[2]}.{parts[3]}",
        })
        {
            var response = await DocumentApi.SendAsync(feed, HttpMethod.Get, $"{Route(api)}?resume_token={Uri.EscapeDataString(token)}");
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.Conflict, "resume_expired"));
        }
    }

    [Test]
    public async Task Invalid_query_arguments_and_unknown_collections_are_rejected()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        foreach (var query in new[]
        {
            "limit=0", "limit=101", "limit=-1", "limit=abc", "limit=1.5", "limit=", "limit=2147483648",
            "limit=1&limit=2", "resume_token=a&resume_token=b", "cursor=abc", "resume_token=",
        })
        {
            var response = await DocumentApi.SendAsync(feed, HttpMethod.Get, $"{Route(api)}?{query}");
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        }

        var missing = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api, collection: "missing"));
        await Assert.That((missing.Status, missing.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        await Assert.That(Changes(await ReadAsync(api, feed))).IsEmpty();
    }

    [Test]
    public async Task The_default_and_maximum_page_size_are_100_with_a_resumable_remainder()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var faker = new Faker();
        var ids = Enumerable.Range(0, 101).Select(_ => DocumentApi.NewId()).ToArray();
        var writes = ids.Select(id => (object)new { op = "create", collection = "items", id, document = new { name = faker.Name.FirstName() } }).ToArray();
        (await api.TransactAsync(writes)).Status.ShouldBe(HttpStatusCode.OK);
        var page = await ReadAsync(api, feed);
        var explicitLimit = await ReadAsync(api, feed, limit: 100);
        await Assert.That(Changes(page).Length).IsEqualTo(100);
        await Assert.That(Changes(page).Select(change => change.GetProperty("id").GetString()).SequenceEqual(ids.Take(100))).IsTrue();
        await Assert.That(HasMore(page)).IsTrue();
        await Assert.That(explicitLimit.Json.GetRawText()).IsEqualTo(page.Json.GetRawText());
        var remainder = await ReadAsync(api, feed, Token(page));
        await Assert.That(Changes(remainder).Length).IsEqualTo(1);
        await Assert.That(Changes(remainder)[0].GetProperty("id").GetString()).IsEqualTo(ids[100]);
        await Assert.That(HasMore(remainder)).IsFalse();
        await Assert.That(Changes(await ReadAsync(api, feed, Token(remainder)))).IsEmpty();
    }

    [Test]
    public async Task The_two_MiB_budget_includes_event_framing_and_preserves_the_remainder()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var faker = new Faker();
        var ids = new[] { DocumentApi.NewId(), DocumentApi.NewId() };
        var documents = new List<string>();
        foreach (var id in ids)
        {
            // Two stored JSON bodies fit exactly in 2 MiB; event framing makes the second a new page.
            var prefix = faker.Random.String2(16, "abcdefghijklmnopqrstuvwxyz");
            var document = "{\"text\":\"" + prefix + new string('x', 1024 * 1024 - 11 - prefix.Length) + "\"}";
            documents.Add(document);
            (await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        }

        var first = await ReadAsync(api, feed, limit: 100);
        await Assert.That(Changes(first).Length).IsEqualTo(1);
        await Assert.That(Changes(first)[0].GetProperty("id").GetString()).IsEqualTo(ids[0]);
        await Assert.That(Changes(first)[0].GetProperty("document").GetRawText()).IsEqualTo(documents[0]);
        await Assert.That(HasMore(first)).IsTrue();
        var second = await ReadAsync(api, feed, Token(first), limit: 100);
        await Assert.That(Changes(second).Length).IsEqualTo(1);
        await Assert.That(Changes(second)[0].GetProperty("id").GetString()).IsEqualTo(ids[1]);
        await Assert.That(Changes(second)[0].GetProperty("document").GetRawText()).IsEqualTo(documents[1]);
        await Assert.That(HasMore(second)).IsFalse();
        foreach (var page in new[] { first, second })
        {
            await Assert.That(Encoding.UTF8.GetByteCount(page.Json.GetRawText())).IsLessThanOrEqualTo(2 * 1024 * 1024);
        }
    }

    [Test]
    public async Task The_feed_requires_the_exact_documents_read_scope_and_is_hidden_on_the_administrative_listener()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        var allowed = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api));
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.OK);
        string?[] scopes = [null, "documents:write", "shapes:read", "documents:read-extra"];
        foreach (var scope in scopes)
        {
            using var client = api.Management.WithToken(Jwt(api, token =>
            {
                if (scope is null)
                {
                    token.Claims.Remove("scope");
                }
                else
                {
                    token.Claims["scope"] = scope;
                }
            }));
            var forbidden = await DocumentApi.SendAsync(client, HttpMethod.Get, Route(api));
            await Assert.That((forbidden.Status, forbidden.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Route(api), UriKind.Relative));
        request.Headers.Authorization = feed.DefaultRequestHeaders.Authorization;
        using var wrongListener = await api.Server.Client.SendAsync(request);
        await Assert.That(wrongListener.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Missing_credentials_and_invalid_JWTs_are_rejected_before_reading_the_feed()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        string?[] credentials =
        [
            null,
            "not-a-jwt",
            Jwt(api, token => token.Audience = "https://another.test"),
            Jwt(api, token => (token.IssuedAt, token.NotBefore, token.Expires) =
                (DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-2))),
        ];
        foreach (var credential in credentials)
        {
            using var client = api.Management.WithToken(credential);
            var refused = await DocumentApi.SendAsync(client, HttpMethod.Get, Route(api));
            await Assert.That(refused.Status).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        var apiKey = await api.SendAsync(HttpMethod.Get, Route(api));
        await Assert.That(apiKey.Status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(Changes(await ReadAsync(api, feed))).IsEmpty();
    }

    [Test]
    public async Task Missing_or_wrong_project_and_database_grants_hide_the_feed_before_scope_checks()
    {
        await using var api = await DocumentApi.StartAsync();
        using var feed = await CreateFeedClientAsync(api);
        using var owner = api.Management.As(api.Tenant.User);
        var otherProject = await api.Management.CreateProjectAsync(api.Tenant.OrganizationId, "Other", owner);
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("other")}/collections", new { name = "items" }))
            .Status.ShouldBe(HttpStatusCode.Created);
        Action<SecurityTokenDescriptor>[] edits =
        [
            token => token.Claims.Remove("project_id"),
            token => token.Claims["project_id"] = otherProject,
            token => token.Claims["database"] = "other",
        ];
        foreach (var edit in edits)
        {
            using var client = api.Management.WithToken(Jwt(api, token =>
            {
                edit(token);
                token.Claims.Remove("scope");
            }));
            var refused = await DocumentApi.SendAsync(client, HttpMethod.Get, Route(api));
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
        }

        using var narrowed = api.Management.WithToken(Jwt(api, token => token.Claims["database"] = "db"));
        var allowed = await DocumentApi.SendAsync(narrowed, HttpMethod.Get, Route(api));
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.OK);
        var hidden = await DocumentApi.SendAsync(narrowed, HttpMethod.Get, Route(api, database: "other"));
        await Assert.That((hidden.Status, hidden.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
    }

    [Test]
    public async Task Openapi_documents_the_unversioned_change_feed_with_TsAuth_security()
    {
        await using var api = await ManagementApi.StartAsync();
        using var anonymous = api.WithToken(null);
        var document = await anonymous.GetFromJsonAsync<JsonElement>(ManagementApi.Uri("/openapi/v1.json"));
        var operation = document.GetProperty("paths")
            .GetProperty("/projects/{project}/databases/{database}/collections/{collection}/changes").GetProperty("get");
        await Assert.That(operation.GetProperty("operationId").GetString()).IsEqualTo("getCollectionChanges");
        await Assert.That(operation.GetProperty("security").EnumerateArray().Any(item => item.TryGetProperty("TsAuth", out _))).IsTrue();
    }

    [Test]
    public async Task An_eight_MiB_memory_budget_refuses_feed_reads_without_leaking_admission()
    {
        // Binding verification reserves 4 MiB; leave room for it while refusing the feed's 32 MiB reservation.
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "8388608");
        using var feed = await CreateFeedClientAsync(api);
        var reserved = await api.Server.MetricAsync("terrabase_memory_reserved_bytes");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var busy = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api));
            await Assert.That((busy.Status, busy.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "change_feed_busy"));
        }

        await Assert.That(await api.Server.MetricAsync("terrabase_change_feed_active")).IsEqualTo(0);
        await Assert.That(await api.Server.MetricAsync("terrabase_memory_reserved_bytes")).IsEqualTo(reserved);
        using var health = await api.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(1, 48L * 1024 * 1024)]
    [Arguments(16, 1024L * 1024 * 1024)]
    public async Task Memory_and_concurrency_admission_stay_held_until_response_delivery(int responses, long budget)
    {
        var delivery = new DeliveryGate(responses);
        await using var api = await DocumentApi.StartAsync(
            settings => settings["memory-budget"] = budget.ToString(CultureInfo.InvariantCulture),
            services => services.AddSingleton<IStartupFilter>(delivery));
        using var feed = await CreateFeedClientAsync(api);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var reserved = await api.Server.MetricAsync("terrabase_memory_reserved_bytes", cancellationToken: deadline.Token);
        var pending = Task.WhenAll(Enumerable.Range(0, responses).Select(_ => DocumentApi.SendAsync(feed,
            HttpMethod.Get, Route(api), configure: request => request.Headers.Add("X-Change-Feed-Test-Hold", "1"), cancellationToken: deadline.Token)));
        try
        {
            await delivery.Entered.Task.WaitAsync(deadline.Token);
            await Assert.That(await api.Server.MetricAsync("terrabase_change_feed_active", cancellationToken: deadline.Token)).IsEqualTo(responses);
            await Assert.That(await api.Server.MetricAsync("terrabase_memory_reserved_bytes", cancellationToken: deadline.Token))
                .IsEqualTo(reserved + responses * 32L * 1024 * 1024);
            var busy = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api), cancellationToken: deadline.Token);
            await Assert.That((busy.Status, busy.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "change_feed_busy"));
        }
        finally
        {
            delivery.Release.TrySetResult();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        foreach (var page in await pending)
        {
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(Changes(page)).IsEmpty();
            await Assert.That(Token(page)).IsNotEmpty();
        }

        // A client can finish receiving just before the server's finally returns its reservation.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await api.Server.MetricAsync("terrabase_change_feed_active", cancellationToken: token) == 0);
        await Assert.That(await api.Server.MetricAsync("terrabase_memory_reserved_bytes")).IsEqualTo(reserved);
        await Assert.That((await ReadAsync(api, feed)).Status).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<HttpClient> CreateFeedClientAsync(DocumentApi api)
    {
        using var owner = api.Management.As(api.Tenant.User);
        await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
        return api.Management.WithToken(Jwt(api));
    }

    private static string Jwt(DocumentApi api, Action<SecurityTokenDescriptor>? edit = null) =>
        api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "documents:read";
            edit?.Invoke(token);
        });

    private static string Route(DocumentApi api, string database = "db", string collection = "items") =>
        $"/projects/{api.Tenant.ProjectId}/databases/{database}/collections/{collection}/changes";

    private static async Task<ApiResponse> ReadAsync(DocumentApi api, HttpClient feed, string? token = null, int? limit = null)
    {
        var query = new List<string>();
        if (token is not null)
        {
            query.Add("resume_token=" + Uri.EscapeDataString(token));
        }
        if (limit is not null)
        {
            query.Add("limit=" + limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        var page = await DocumentApi.SendAsync(feed, HttpMethod.Get, Route(api) + (query.Count == 0 ? "" : "?" + string.Join('&', query)));
        page.Status.ShouldBe(HttpStatusCode.OK);
        return page;
    }

    private static JsonElement[] Changes(ApiResponse page) => [.. page.Json.GetProperty("changes").EnumerateArray()];

    private static string Token(ApiResponse page) => page.Json.GetProperty("resume_token").GetString()!;

    private static bool HasMore(ApiResponse page) => page.Json.GetProperty("has_more").GetBoolean();

    private static long Number(JsonElement value, string property) =>
        long.Parse(value.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    // Hold real successful responses at the first transport write, after the page was prepared.
    // This changes delivery timing only; storage, authorization and memory admission stay real.
    private sealed class DeliveryGate(int responses) : IStartupFilter
    {
        private int _entered;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Headers.ContainsKey("X-Change-Feed-Test-Hold"))
                {
                    context.Response.OnStarting(async () =>
                    {
                        if (context.Response.StatusCode == (int)HttpStatusCode.OK)
                        {
                            if (Interlocked.Increment(ref _entered) == responses)
                            {
                                Entered.TrySetResult();
                            }
                            await Release.Task.WaitAsync(context.RequestAborted);
                        }
                    });
                }
                await continuation(context);
            });
            next(app);
        };
    }
}
