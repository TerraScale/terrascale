using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseResponseTests
{
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
            var error = await TerraBaseException.FromResponseAsync(status, content, CancellationToken.None);
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
        var json = $"{{\"name\":\"items\",\"created_at\":\"{timestamp}\"}}";
        await Assert.That(() => JsonSerializer.Deserialize(json, TerraBaseJsonContext.Wire.TerraBaseCollection)).Throws<JsonException>();
    }
}

[ClassDataSource<TerraBaseServer>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseResponseIntegrationTests(TerraBaseServer server)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RealSuccessResponseHonorsTheExactByteLimit(bool oneByteOver)
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "bounds");
        await database.CreateAsync();
        await database.CreateCollectionAsync("items");
        using var document = JsonDocument.Parse("{\"text\":\"" + new string('x', 64 * 1024) + "\"}");
        var created = await database.CreateDocumentAsync("items", document.RootElement);
        var path = $"/v1/projects/{tenant.Project.Id}/databases/bounds/collections/items/documents/{created.Id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Running.ClientAddress, path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tenant.ApiKey);
        using var measured = await server.Running.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        measured.EnsureSuccessStatusCode();
        var bytes = await measured.Content.ReadAsByteArrayAsync();
        var connection = new TerraBaseConnection(server.Running.Http, server.Running.ClientAddress, null,
            tenant.ApiKey, "ApiKey", TimeSpan.FromSeconds(30), bytes.Length - (oneByteOver ? 1 : 0));

        var call = () => connection.SendAsync(HttpMethod.Get, path, null,
            TerraBaseJsonContext.Wire.TerraBaseDocument, null, CancellationToken.None);
        if (oneByteOver)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(call);
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(error.Message).Contains("larger than MaxResponseBytes");
        }
        else
        {
            var (result, status) = await call();
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Id).IsEqualTo(created.Id);
            await Assert.That(JsonElement.DeepEquals(result.Document, document.RootElement)).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlierHttpClientTimeoutIsDistinctFromCallerCancellation(bool callerCancels)
    {
        server.Require();
        using var handler = TerraBaseHttp.CreateHandler();
        var trusted = server.Running.Certificate.RawData;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(trusted);
        using var http = new HttpClient(new DelayedHandler(handler)) { Timeout = TimeSpan.FromMilliseconds(200) };
        var management = new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions
        {
            BaseAddress = server.Running.ClientAddress, ServiceCredential = server.Running.ServiceCredential,
        });
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

    private sealed class DelayedHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Delay dispatch; the underlying transport is real and no HTTP response is fabricated.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task BothClientsResolveTheConfiguredEndpointPerRequest()
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var resolver = new TerraBaseClientOptionsTests.CountingResolver(server.Running.ClientAddress);
        var management = new TerraBaseManagementClient(server.Running.Http, new TerraBaseManagementClientOptions
        {
            EndpointResolver = resolver, ServiceCredential = server.Running.ServiceCredential,
        });
        await management.GetOrganizationAsync(tenant.UserId, tenant.Organization.Id);
        var documents = new TerraBaseDocumentsClient(server.Running.Http, new TerraBaseDocumentsClientOptions
        {
            EndpointResolver = resolver, ApiKey = tenant.ApiKey,
        });
        var database = documents.Database(tenant.Project.Id, "resolver");
        await database.CreateAsync();
        await database.CreateCollectionAsync("items");
        await Assert.That(resolver.Calls).IsEqualTo(3);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task CancellationDuringResponseProcessingCannotReturnSuccess(bool deadlineExpires, bool parsingFails)
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "cancellation");
        await database.CreateAsync();
        await database.CreateCollectionAsync("items");
        using var canceled = new CancellationTokenSource();
        var parsed = false;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ProcessingConverter(() =>
        {
            parsed = true;
            if (deadlineExpires)
            {
                // Let the one-second request deadline elapse inside synchronous JSON processing.
                Thread.Sleep(TimeSpan.FromSeconds(1.1));
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
        var connection = new TerraBaseConnection(server.Running.Http, server.Running.ClientAddress, null, tenant.ApiKey,
            "ApiKey", TimeSpan.FromSeconds(1), TerraBaseConnection.DefaultMaxResponseBytes);
        var path = $"/v1/projects/{tenant.Project.Id}/databases/cancellation/collections/items";
        var call = () => connection.SendAsync(HttpMethod.Get, path, null, typeInfo, null, canceled.Token);
        if (deadlineExpires)
        {
            await Assert.That(call).Throws<TimeoutException>();
        }
        else
        {
            await Assert.That(call).Throws<OperationCanceledException>();
        }

        await Assert.That(parsed).IsTrue();
    }

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
