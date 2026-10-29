using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseResponseTests
{
    private const string RequestId = "01JA0000000000000000000001";

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("oversized", false)]
    [Arguments("01JA0000000000000000000001", false)]
    [Arguments("01JA0000000000000000000001", true)]
    [Arguments("01ja0000000000000000000001", false)]
    [Arguments("81JA0000000000000000000001", false)]
    [Arguments("01JA000000000000000000000I", false)]
    [Arguments("01JA0000000000000000000001,01JA0000000000000000000001", false)]
    [Arguments(" 01JA0000000000000000000001", false)]
    public async Task Request_identity_accepts_only_one_canonical_response_ULID(string? header, bool duplicate)
    {
        using var response = TerraBaseIntegrationFixture.Json("{", HttpStatusCode.BadRequest);
        if (header == "oversized") header = new string('A', 4096);
        if (header is not null) response.Headers.TryAddWithoutValidation("x-request-id", duplicate ? [header, header] : [header]);
        var dataError = await TerraBaseException.FromResponseAsync(response, CancellationToken.None);
        var authError = await TerraScaleAuthErrors.CreateAsync(response, CancellationToken.None);
        var expected = header == RequestId && !duplicate ? RequestId : null;
        await Assert.That(dataError.RequestId).IsEqualTo(expected);
        await Assert.That(authError.RequestId).IsEqualTo(expected);
        await Assert.That(dataError.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(authError.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments("{", false)]
    [Arguments("null", false)]
    [Arguments("{}", true)]
    public async Task Malformed_success_and_oversized_bodies_retain_identity_after_disposal(string body, bool oversized)
    {
        var propagated = false;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            propagated = request.Headers.Contains("X-Request-ID");
            var response = TerraBaseIntegrationFixture.Json(oversized ? new string(' ', 64 * 1024) + body : body);
            response.Headers.TryAddWithoutValidation("X-Request-ID", RequestId);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseDocumentsClient(new TerraBaseConnection(http, TerraBaseRegion.PublicOrigin, null, null, "credential",
            TerraBaseConnection.RequestDeadline, 64 * 1024, _ => ValueTask.FromResult("test-token"), region: "sjc"));
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.Database(TerraBaseIntegrationFixture.ProjectId, "app").ListCollectionsAsync());
        await Assert.That(error!.RequestId).IsEqualTo(RequestId);
        await Assert.That(error.ResponseHeaders["X-Request-ID"].Single()).IsEqualTo(RequestId);
        await Assert.That(error.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(propagated).IsFalse();
    }

    [Test]
    [Arguments(70)]
    [Arguments(1000)]
    [Arguments(1009)]
    public async Task Retained_document_reads_accept_producer_depth_and_reject_excess(int depth)
    {
        var document = NestedDocument(depth);
        var json = """{"id":"a","version":"tbv2-retained","state":"value","document": """ + document + "}";
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(json)));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        if (depth > 1008)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.ReadDocumentAsync("items", "01JA00000000000000000000D1"));
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            return;
        }

        var result = await db.ReadDocumentAsync("items", "01JA00000000000000000000D1");
        await Assert.That(result.Document!.Document.GetRawText()).IsEqualTo(document);
    }

    [Test]
    [Arguments(70)]
    [Arguments(1000)]
    [Arguments(1009)]
    public async Task Shape_responses_accept_retained_document_depth_and_reject_excess(int depth)
    {
        var document = NestedDocument(depth);
        var json = """
            {"shape_id":"shape","handle":"h1-retained","offset":"0","next_offset":"1","covered_offset":"1",
             "values_at":"1","up_to_date":true,"cursor_resume":"resume","changes":[{"offset":"1",
             "ops":[{"op":"upsert","id":"a","version":"tbv2-retained","document":
            """ + document + """}]}]}""";
        if (depth > 1008)
        {
            await InvalidResponseAsync(json, TerraBaseShapeJsonContext.Default.ShapeBody);
            return;
        }

        var result = TerraBaseConnection.ReadResponse(Encoding.UTF8.GetBytes(json), TerraBaseShapeJsonContext.Default.ShapeBody, HttpStatusCode.OK);
        await Assert.That(result.Changes.Single().Ops.Single().Document!.Value.GetRawText()).IsEqualTo(document);
    }

    private static string NestedDocument(int depth) =>
        string.Concat(Enumerable.Repeat("{\"value\":", depth)) + "0" + new string('}', depth);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnreadableErrorStreamsPreserveStatusSpecificExceptions(bool oversized)
    {
        // Exercise the same bounded stream reader used by SendAsync, without fabricating HTTP responses.
        var json = oversized
            ? "{\"error\":{\"code\":\"ignored\",\"message\":\"" + new string('x', 64 * 1024) + "\"}}"
            : "{\"error\":";
        foreach (var (status, type) in new (HttpStatusCode, Type)[]
        {
            (HttpStatusCode.BadRequest, typeof(TerraBaseValidationException)),
            (HttpStatusCode.RequestEntityTooLarge, typeof(TerraBaseValidationException)),
            (HttpStatusCode.UnsupportedMediaType, typeof(TerraBaseValidationException)),
            (HttpStatusCode.Unauthorized, typeof(TerraBaseAuthenticationException)),
            (HttpStatusCode.Forbidden, typeof(TerraBaseForbiddenException)),
            (HttpStatusCode.NotFound, typeof(TerraBaseNotFoundException)),
            (HttpStatusCode.Conflict, typeof(TerraBaseConflictException)),
            (HttpStatusCode.PreconditionFailed, typeof(TerraBasePreconditionFailedException)),
            (HttpStatusCode.ServiceUnavailable, typeof(TerraBaseUnavailableException)),
            (HttpStatusCode.InternalServerError, typeof(TerraBaseException)),
        })
        {
            using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            using var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("X-Request-ID", RequestId);
            var error = await TerraBaseException.FromResponseAsync(response, CancellationToken.None);
            await Assert.That(error.RequestId).IsEqualTo(RequestId);
            await Assert.That(error.GetType()).IsEqualTo(type);
            await Assert.That(error.StatusCode).IsEqualTo(status);
            await Assert.That(error.ErrorCode).IsNull();
            await Assert.That(error.OperationIndex).IsNull();
            await Assert.That(error.Message).IsEqualTo($"TerraBase answered HTTP {(int)status}.");
        }
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"items\":null,\"next_cursor\":null}")]
    [Arguments("null")]
    public Task IncompleteOrNullDocumentPagesAreInvalidResponses(string json) =>
        InvalidResponseAsync(json, TerraBaseJsonContext.Wire.TerraBaseDocumentPage);

    [Test]
    [Arguments("{}")]
    [Arguments("{\"id\":null,\"version\":\"tbv1-version\",\"offset\":\"1\",\"durability\":\"local_durable\"}")]
    [Arguments("{\"id\":\"id\",\"version\":null,\"offset\":\"1\",\"durability\":\"local_durable\"}")]
    public Task IncompleteOrNullWriteResultsAreInvalidResponses(string json) =>
        InvalidResponseAsync(json, TerraBaseJsonContext.Wire.TerraBaseWriteResult);

    [Test]
    [Arguments("{}")]
    [Arguments("{\"invitation\":null,\"token\":\"secret\"}")]
    [Arguments("{\"invitation\":{},\"token\":\"secret\"}")]
    public Task IncompleteOrNullManagementResultsAreInvalidResponses(string json) =>
        InvalidResponseAsync(json, TerraBaseJsonContext.Wire.TerraBaseCreatedInvitation);

    [Test]
    [Arguments("0")]
    [Arguments("999")]
    [Arguments("\"0\"")]
    [Arguments("\"999\"")]
    public Task NumericPlansAreInvalidResponses(string plan) =>
        InvalidResponseAsync($$"""{"id":"org","name":"Acme","createdBy":"user","createdAt":"2026-01-01T00:00:00Z","plan":{{plan}}}""",
            TerraBaseJsonContext.Wire.TerraBaseOrganization);

    [Test]
    [Arguments("0")]
    [Arguments("999")]
    [Arguments("\"0\"")]
    [Arguments("\"999\"")]
    public Task NumericRolesAreInvalidResponses(string role) =>
        InvalidResponseAsync($$"""{"userId":"user","role":{{role}},"joinedAt":"2026-01-01T00:00:00Z"}""",
            TerraBaseJsonContext.Wire.TerraBaseMember);

    [Test]
    [Arguments(TerraBasePlan.Hobby, "hobby", TerraBaseRole.Member, "member")]
    [Arguments(TerraBasePlan.Pro, "pro", TerraBaseRole.Admin, "admin")]
    [Arguments(TerraBasePlan.Enterprise, "enterprise", TerraBaseRole.Owner, "owner")]
    public async Task NamedManagementEnumsKeepTheirLowercaseWireStrings(TerraBasePlan plan, string planName, TerraBaseRole role, string roleName)
    {
        var body = new RequestBody { Plan = plan, Role = role };
        var json = JsonSerializer.Serialize(body, TerraBaseJsonContext.Wire.RequestBody);
        await Assert.That(json).IsEqualTo($$"""{"plan":"{{planName}}","role":"{{roleName}}"}""");
        var parsed = JsonSerializer.Deserialize(json, TerraBaseJsonContext.Wire.RequestBody)!;
        await Assert.That(parsed.Plan).IsEqualTo(plan);
        await Assert.That(parsed.Role).IsEqualTo(role);
    }

    [Test]
    public async Task UndefinedManagementEnumsCannotSerializeAsIntegers()
    {
        await Assert.That(() => JsonSerializer.Serialize(new RequestBody { Plan = (TerraBasePlan)999 }, TerraBaseJsonContext.Wire.RequestBody))
            .Throws<JsonException>();
        await Assert.That(() => JsonSerializer.Serialize(new RequestBody { Role = (TerraBaseRole)999 }, TerraBaseJsonContext.Wire.RequestBody))
            .Throws<JsonException>();
    }

    [Test]
    [Arguments("{\"items\":[null],\"next_cursor\":null}")]
    [Arguments("{\"items\":[{\"id\":\"id\",\"version\":\"version\",\"state\":\"value\",\"document\":{}},null],\"next_cursor\":null}")]
    public Task NullDocumentPageItemsAreInvalidResponses(string json) =>
        InvalidResponseAsync(json, TerraBaseJsonContext.Wire.TerraBaseDocumentPage);

    [Test]
    public Task NullCollectionPageItemsAreInvalidResponses() =>
        InvalidResponseAsync("{\"items\":[null],\"next_cursor\":null}", TerraBaseJsonContext.Wire.TerraBaseCollectionPage);

    [Test]
    public Task NullTransactionResultsAreInvalidResponses() =>
        InvalidResponseAsync("{\"offset\":\"1\",\"durability\":\"local_durable\",\"results\":[{\"asserted\":true},null]}",
            TerraBaseJsonContext.Wire.TerraBaseTransactionResult);

    [Test]
    public async Task NullManagementPageItemsAreInvalidResponsesForEveryItemType()
    {
        const string json = "{\"items\":[null],\"next\":null}";
        var context = TerraBaseJsonContext.Wire;
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseOrganization);
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseProject);
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseManagedDatabase);
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseMember);
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseInvitation);
        await InvalidResponseAsync(json, context.TerraBasePageTerraBaseAuditEvent);
    }

    [Test]
    public async Task EmptyResponseCollectionsRemainValid()
    {
        var context = TerraBaseJsonContext.Wire;
        var documents = TerraBaseConnection.ReadResponse("{\"items\":[],\"next_cursor\":null}"u8, context.TerraBaseDocumentPage, HttpStatusCode.OK);
        var collections = TerraBaseConnection.ReadResponse("{\"items\":[],\"next_cursor\":null}"u8, context.TerraBaseCollectionPage, HttpStatusCode.OK);
        var organizations = TerraBaseConnection.ReadResponse("{\"items\":[],\"next\":null}"u8, context.TerraBasePageTerraBaseOrganization, HttpStatusCode.OK);
        await Assert.That(documents.Items).IsEmpty();
        await Assert.That(collections.Items).IsEmpty();
        await Assert.That(organizations.Items).IsEmpty();
    }

    [Test]
    public async Task NullableTransactionResultFieldsMayBeOmitted()
    {
        var result = TerraBaseConnection.ReadResponse(
            "{\"offset\":\"1\",\"durability\":\"local_durable\",\"results\":[{\"asserted\":true}]}"u8,
            TerraBaseJsonContext.Wire.TerraBaseTransactionResult, HttpStatusCode.OK);
        await Assert.That(result.Results.Single().Asserted).IsTrue();
        await Assert.That(result.Results.Single().Id).IsNull();
    }

    [Test]
    [Arguments("tbv1-0123456789abcdef0123456789abcdef")]
    [Arguments("tbv2-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [Arguments("tbv2-AZx3_k-9QmLrT0aVb7c2e1f0.Hq")]
    public async Task VersionTokensAreOpaqueInResponsesAndIfMatch(string version)
    {
        var context = TerraBaseJsonContext.Wire;
        var document = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes($$$"""{"id":"a","version":"{{{version}}}","state":"value","document":{}}"""), context.TerraBaseDocument, HttpStatusCode.OK);
        var write = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes($$"""{"id":"a","version":"{{version}}","offset":"1","durability":"local_durable"}"""), context.TerraBaseWriteResult, HttpStatusCode.OK);
        await Assert.That(document.Version).IsEqualTo(version);
        await Assert.That(write.Version).IsEqualTo(version);

        using var request = new HttpRequestMessage();
        TerraBasePrecondition.Matches(document.Version).Apply(request.Headers);
        await Assert.That(request.Headers.IfMatch.ToString()).IsEqualTo($"\"{version}\"");
        var operation = JsonSerializer.Serialize(new TerraBaseTransaction([TerraBaseTransactionOperation.Delete("items", "01JA00000000000000000000D1", version)]), context.TerraBaseTransaction);
        await Assert.That(operation).Contains($"\"if_match\":\"{version}\"");
    }

    [Test]
    public async Task CausalConflictReadsKeepTheWinnerAndIgnoreSiblings()
    {
        var page = TerraBaseConnection.ReadResponse("""
            {"items":[{"id":"a","version":"tbv2-c0ffee","state":"conflict","document":{"v":2},
              "siblings":[{"version":"tbv2-1","document":{"v":1}},{"version":"tbv2-2","document":{"v":2},"future":[1]}]}],
             "next_cursor":null}
            """u8, TerraBaseJsonContext.Wire.TerraBaseDocumentPage, HttpStatusCode.OK);
        var document = page.Items.Single();
        await Assert.That(document.State).IsEqualTo("conflict");
        await Assert.That(document.Document.GetProperty("v").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("local_durable")]
    [Arguments("synced_durable")]
    [Arguments("object_durable")]
    [Arguments("some_future_label")]
    public async Task AnyDurabilityLabelIsAccepted(string durability)
    {
        var context = TerraBaseJsonContext.Wire;
        var write = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes($$"""{"id":"a","version":"tbv2-1","offset":"1","durability":"{{durability}}","hlc":"x"}"""), context.TerraBaseWriteResult, HttpStatusCode.OK);
        var transaction = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes($$"""{"offset":"1","durability":"{{durability}}","results":[{"id":"a","version":"tbv2-1"}]}"""), context.TerraBaseTransactionResult, HttpStatusCode.OK);
        await Assert.That(write.Durability).IsEqualTo(durability);
        await Assert.That(transaction.Durability).IsEqualTo(durability);
    }

    private static async Task InvalidResponseAsync<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        var error = await Assert.ThrowsAsync<TerraBaseException>(() =>
        {
            _ = TerraBaseConnection.ReadResponse(Encoding.UTF8.GetBytes(json), typeInfo, HttpStatusCode.OK);
            return Task.CompletedTask;
        });
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments("9223372036854775807")]
    [Arguments("253402300800000")]
    public async Task OutOfRangeCollectionTimestampIsAJsonError(string timestamp)
    {
        var json = $"{{\"name\":\"items\",\"created_at\":\"{timestamp}\",\"id\":\"01JA0000000000000000000002\"}}";
        await Assert.That(() => JsonSerializer.Deserialize(json, TerraBaseJsonContext.Wire.TerraBaseCollection)).Throws<JsonException>();
    }
}

/// <summary>Response bounds, deadlines and resolution over a fake transport; no TerraBase runs in this repository.</summary>
public sealed class TerraBaseResponseIntegrationTests
{
    private static readonly Uri Origin = new("https://terrabase.example.test/");
    private static readonly Uri Private = new("http://100.64.0.1:8081/");
    private const string DocumentPath = "/v1/projects/01JA0000000000000000000001/databases/bounds/collections/items/documents/by-key?id=01JA0000000000000000000009";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessResponseHonorsTheExactByteLimit(bool oneByteOver)
    {
        var body = "{\"id\":\"01JA0000000000000000000009\",\"version\":\"v1\",\"state\":\"live\",\"document\":{\"text\":\"" + new string('x', 64 * 1024) + "\"}}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
        using var handler = Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, Origin, null, "tb_test_key", "ApiKey", TimeSpan.FromSeconds(30), bytes - (oneByteOver ? 1 : 0));

        var call = () => connection.SendAsync(HttpMethod.Get, DocumentPath, null, TerraBaseJsonContext.Wire.TerraBaseDocument, null, CancellationToken.None);
        if (oneByteOver)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(call);
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(error.Message).Contains("larger than the supported response bound");
        }
        else
        {
            var (result, status) = await call();
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Id).IsEqualTo("01JA0000000000000000000009");
            await Assert.That(result.Document.GetProperty("text").GetString()!.Length).IsEqualTo(64 * 1024);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlierHttpClientTimeoutIsDistinctFromCallerCancellation(bool callerCancels)
    {
        using var handler = new TerraBaseTestHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        // The other deadline is far away, so a loaded runner cannot make the two race.
        using var http = new HttpClient(handler) { Timeout = callerCancels ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(200) };
        var management = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Private });
        using var caller = new CancellationTokenSource();
        if (callerCancels)
        {
            caller.CancelAfter(TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAsync<OperationCanceledException>(() => management.ListOrganizationsAsync("user", cancellationToken: caller.Token));
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => management.ListOrganizationsAsync("user"));
            await Assert.That(error!.Message).Contains("HttpClient.Timeout");
        }
    }

    [Test]
    public async Task BothClientsResolveTheConfiguredEndpointPerRequest()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException("no TerraBase here"));
        using var http = new HttpClient(handler);
        var resolver = new TerraBaseClientOptionsTests.CountingResolver(Origin);
        var adminResolver = new TerraBaseClientOptionsTests.CountingResolver(Private);
        var management = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { EndpointResolver = adminResolver });
        var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { EndpointResolver = resolver, ApiKey = "tb_test_key" })
            .Database("01JA0000000000000000000001", "resolver");

        await Assert.ThrowsAsync<HttpRequestException>(() => management.GetOrganizationAsync("user", "01JA0000000000000000000002"));
        await Assert.ThrowsAsync<HttpRequestException>(() => database.CreateAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => database.CreateCollectionAsync("items"));
        await Assert.That(adminResolver.Calls).IsEqualTo(1);
        await Assert.That(resolver.Calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task CancellationDuringResponseProcessingCannotReturnSuccess(bool deadlineExpires, bool parsingFails)
    {
        using var handler = Answer(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new TerraBaseCollection("items", DateTimeOffset.UnixEpoch, "01JA0000000000000000000002"), TerraBaseJsonContext.Wire.TerraBaseCollection), Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        using var canceled = new CancellationTokenSource();
        var clock = new ResponseDeadlineClock();
        var parsed = false;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ProcessingConverter(() =>
        {
            parsed = true;
            if (deadlineExpires)
            {
                // Fire the deadline inside parsing without relying on ThreadPool timer scheduling.
                clock.Expire();
            }
            else
            {
                canceled.Cancel();
            }

            if (parsingFails)
            {
                throw new JsonException("Response parsing failed after cancellation.");
            }
        }));
        var typeInfo = new TerraBaseJsonContext(options).TerraBaseCollection;
        var connection = new TerraBaseConnection(http, Origin, null, "tb_test_key", "ApiKey", TimeSpan.FromSeconds(1), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var call = () => connection.SendAsync(HttpMethod.Get, "/v1/projects/01JA0000000000000000000001/databases/cancellation/collections/items", null, typeInfo, null, canceled.Token);
        if (deadlineExpires)
        {
            await Assert.That(call).Throws<TimeoutException>();
        }
        else
        {
            await Assert.That(call).Throws<OperationCanceledException>();
        }

        await Assert.That(parsed).IsTrue();
        await Assert.That(clock.DueTime).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    private sealed class ResponseDeadlineClock : TimeProvider
    {
        private ResponseDeadlineTimer? timer;
        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return timer = new ResponseDeadlineTimer(callback, state);
        }

        public void Expire() => (timer ?? throw new InvalidOperationException("The request deadline has not been created.")).Fire();

        private sealed class ResponseDeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private TimerCallback? pending = callback;
            public void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref pending, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static TerraBaseTestHandler Answer(Func<HttpResponseMessage> response) => new((_, _) => Task.FromResult(response()));

    private sealed class ProcessingConverter(Action processed) : JsonConverter<TerraBaseCollection>
    {
        public override TerraBaseCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var collection = JsonSerializer.Deserialize(ref reader, TerraBaseJsonContext.Wire.TerraBaseCollection)!;
            processed();
            return collection;
        }

        public override void Write(Utf8JsonWriter writer, TerraBaseCollection value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
