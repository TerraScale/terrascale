using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleManagementClientTests
{
    private static readonly string[] Operations =
    [
        "insert_unique", "compare_and_swap", "consume_one", "consume_matching", "count", "delete_many", "delete_one", "increment_one", "update_many", "update_one", "index", "find_page", "find_one", "begin_transaction", "commit_transaction", "rollback_transaction",
        "database_usage", "get_schema", "put_schema", "list_indexes", "create_index", "delete_index", "index_recommendations",
        "list_webhooks", "create_webhook", "delete_webhook", "list_deliveries", "replay_webhook", "rotate_secret",
        "list_exports", "create_export", "get_export", "resume_export", "download_export", "cancel_export",
        "development_fault", "clear_development_fault", "development_logs", "get_provisioning_operation", "create_database",
    ];

    [Test]
    public async Task NamedOperationsConsumeEveryProducerSuccessAndFailure()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", "http.json"));
        using var corpus = await JsonDocument.ParseAsync(stream);
        foreach (var operationId in Operations)
        {
            var operation = corpus.RootElement.GetProperty("operations").EnumerateArray()
                .Single(value => value.GetProperty("operationId").GetString() == operationId);
            foreach (var response in operation.GetProperty("responses").EnumerateArray())
            {
                using var handler = new CorpusHandler(response);
                using var http = new HttpClient(handler);
                var client = Client(http);
                var result = await Invoke(client, operationId, operation);
                await Assert.That(handler.Attempts).IsEqualTo(1);
                await Assert.That(handler.Method).IsEqualTo(operation.GetProperty("request").GetProperty("method").GetString());
                var expectedPath = System.Text.RegularExpressions.Regex.Replace(TerraScaleContractRoutes.Find(operationId).PathTemplate, "\\{[^}]+\\}", "sample");
                await Assert.That(handler.Path).IsEqualTo(expectedPath);
                if (operationId == "put_schema")
                {
                    await Assert.That(handler.IdempotencyKey).IsEqualTo("schema-key");
                    await Assert.That(handler.IfMatch).IsEqualTo("\"1\"");
                }
                if (operationId == "create_database")
                {
                    using var body = JsonDocument.Parse(handler.Body!);
                    await Assert.That(body.RootElement.GetProperty("operation_id").GetString()).IsEqualTo("operation-key");
                }
                if (IsAuthPersistenceOperation(operationId))
                {
                    await Assert.That(handler.IdempotencyKey).IsEqualTo(operationId == "find_one" ? null : "auth-key");
                    await Assert.That(handler.AuthTransaction).IsEqualTo(operationId is "find_one" or "begin_transaction" or "commit_transaction" or "rollback_transaction" ? null : "transaction-key");
                }
                if (operationId == "create_database")
                {
                    if (response.GetProperty("status").GetInt32() == 202)
                        await Assert.That(result).IsTypeOf<TerraScaleDatabaseAccepted>();
                    else
                        await Assert.That(result).IsTypeOf<TerraScaleDatabaseCreated>();
                }
                if (operationId == "download_export")
                    await Assert.That(Encoding.UTF8.GetString((byte[])result!)).IsEqualTo(response.GetProperty("body").GetString());
            }
            foreach (var response in operation.GetProperty("errors").EnumerateArray())
            {
                using var handler = new CorpusHandler(response);
                using var http = new HttpClient(handler);
                var client = Client(http);
                var error = await Assert.ThrowsAsync<TerraScaleApiException>(() => Invoke(client, operationId, operation));
                await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)response.GetProperty("status").GetInt32());
                await Assert.That(error.ErrorCode).IsEqualTo(response.GetProperty("body").GetProperty("error").GetProperty("code").GetString());
                await Assert.That(handler.Attempts).IsEqualTo(1);
                if (IsAuthPersistenceOperation(operationId))
                {
                    await Assert.That(handler.IdempotencyKey).IsEqualTo(operationId == "find_one" ? null : "auth-key");
                    await Assert.That(handler.AuthTransaction).IsEqualTo(operationId is "find_one" or "begin_transaction" or "commit_transaction" or "rollback_transaction" ? null : "transaction-key");
                }
            }
        }
    }

    [Test]
    public async Task ExportDownloadValidatesArtifactMetadata()
    {
        foreach (var variant in new[] { "03", "4", "3, 3" })
        {
            using var http = new HttpClient(new ArtifactHandler(variant, new string('a', 64), [1, 2, 3]));
            var client = Client(http);
            await Assert.That(async () => await client.Management.Exports.DownloadArtifactAsync("db", "job", "member"))
                .Throws<Exception>();
        }
    }

    [Test]
    public async Task UInt64UsageAndRequiredFieldsRetainThePublishedContract()
    {
        const string json = "{\"tenant_id\":\"tenant\",\"database_id\":\"database\",\"snapshot_sequence\":18446744073709551615,\"storage_complete_through_millis\":null,\"meters\":[{\"meter\":\"document_write_units\",\"units\":\"18446744073709551616\"},{\"meter\":\"storage_byte_seconds\",\"units\":\"0\"}],\"reads\":{\"total_reads\":18446744073709551615}}";
        var summary = JsonSerializer.Deserialize(json, TerraScaleContractJsonContext.Default.UsageSummary)!;
        await Assert.That(summary.SnapshotSequence).IsEqualTo(ulong.MaxValue);
        await Assert.That(summary.Reads.TotalReads).IsEqualTo(ulong.MaxValue);
        await Assert.That(summary.Meters[0].Units).IsEqualTo("18446744073709551616");
        await Assert.That(summary.StorageCompleteThroughMillis).IsNull();
        await Assert.ThrowsAsync<JsonException>(() =>
        {
            _ = JsonSerializer.Deserialize("{}", TerraScaleContractJsonContext.Default.UsageSummary);
            return Task.CompletedTask;
        });
    }

    private static T Request<T>(JsonElement operation, JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(operation.GetProperty("request").GetProperty("body").GetProperty("value").GetRawText(), type)!;

    private static bool IsAuthPersistenceOperation(string operationId) => operationId is
        "insert_unique" or "compare_and_swap" or "consume_one" or "consume_matching" or "count" or
        "delete_many" or "delete_one" or "increment_one" or "update_many" or "update_one" or
        "index" or "find_page" or "find_one" or "begin_transaction" or "commit_transaction" or
        "rollback_transaction";

    private static async Task<object?> Invoke(TerraScaleClient client, string operationId, JsonElement operation)
    {
        var management = client.Management;
        switch (operationId)
        {
            case "insert_unique": return await client.AuthPersistence.InsertUniqueAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.UniqueInsert), "auth-key", "transaction-key");
            case "compare_and_swap": return await client.AuthPersistence.CompareAndSwapAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.ConditionalUpdate), "auth-key", "transaction-key");
            case "consume_one": return await client.AuthPersistence.ConsumeOneAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.Consume), "auth-key", "transaction-key");
            case "consume_matching": return await client.AuthPersistence.ConsumeMatchingAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateOnly), "auth-key", "transaction-key");
            case "count": return await client.AuthPersistence.CountAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateOnly), "auth-key", "transaction-key");
            case "delete_many": return await client.AuthPersistence.DeleteManyAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateOnly), "auth-key", "transaction-key");
            case "delete_one": return await client.AuthPersistence.DeleteOneAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateOnly), "auth-key", "transaction-key");
            case "increment_one": return await client.AuthPersistence.IncrementOneAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.Increment), "auth-key", "transaction-key");
            case "update_many": return await client.AuthPersistence.UpdateManyAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateUpdate), "auth-key", "transaction-key");
            case "update_one": return await client.AuthPersistence.UpdateOneAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PredicateUpdate), "auth-key", "transaction-key");
            case "index": return await client.AuthPersistence.CreateIndexAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.UniqueIndex), "auth-key", "transaction-key");
            case "find_page": return await client.AuthPersistence.FindPageAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.PageQuery), "auth-key", "transaction-key");
            case "find_one": return await client.AuthPersistence.GetAsync("sample", "sample", "sample");
            case "begin_transaction": return await client.AuthPersistence.BeginTransactionAsync("sample", "transaction-key", "auth-key");
            case "commit_transaction": return await client.AuthPersistence.CommitTransactionAsync("sample", "transaction-key", "auth-key");
            case "rollback_transaction": return await client.AuthPersistence.RollbackTransactionAsync("sample", "transaction-key", "auth-key");
            case "database_usage": return await management.Usage.GetAsync("sample");
            case "get_schema": return await management.Schema.GetAsync("sample", "sample");
            case "put_schema": return await management.Schema.PutAsync("sample", "sample", Request(operation, TerraScaleContractJsonContext.Default.CollectionSchema), "schema-key", 1);
            case "list_indexes": return await management.Indexes.ListAsync("sample", "sample");
            case "create_index": return await management.Indexes.CreateAsync("sample", "sample", Request(operation, TerraScaleContractJsonContext.Default.CreateIndexRequest));
            case "delete_index": await management.Indexes.DeleteAsync("sample", "sample", "sample"); return null;
            case "index_recommendations": return await management.Indexes.RecommendationsAsync("sample", "sample");
            case "list_webhooks": return await management.Webhooks.ListAsync("sample");
            case "create_webhook": return await management.Webhooks.CreateAsync("sample", "https://receiver.test/hook");
            case "rotate_secret": return await management.Webhooks.RotateSecretAsync("sample", "sample");
            case "delete_webhook": return await management.Webhooks.DeleteAsync("sample", "sample");
            case "list_deliveries": return await management.Webhooks.ListDeliveriesAsync("sample", "sample");
            case "replay_webhook": return await management.Webhooks.ReplayAsync("sample", "sample", "sample");
            case "list_exports": return await management.Exports.ListAsync("sample");
            case "create_export": return await management.Exports.CreateAsync("sample", "export-key");
            case "get_export": return await management.Exports.GetAsync("sample", "sample");
            case "resume_export": return await management.Exports.ResumeAsync("sample", "sample", "cursor");
            case "cancel_export": return await management.Exports.CancelAsync("sample", "sample");
            case "download_export":
                return (await management.Exports.DownloadArtifactAsync("sample", "sample", "sample")).Bytes;
            case "development_fault": return await management.Development.SetFaultAsync("sample", Request(operation, TerraScaleContractJsonContext.Default.DevelopmentFaultRequest));
            case "clear_development_fault": return await management.Development.ClearFaultAsync("sample");
            case "development_logs": return await management.Development.LogsAsync("sample");
            case "get_provisioning_operation": return await client.Operations.GetAsync("sample");
            case "create_database": return await client.Databases.CreateAsync("sample", ["GRU"], "operation-key", "br-only");
            default: throw new InvalidOperationException("Missing named operation dispatcher: " + operationId);
        }
    }

    private static TerraScaleClient Client(HttpClient http) => new(http, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
    });

    private sealed class CorpusHandler(JsonElement fixture) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? AuthTransaction { get; private set; }
        public string? IfMatch { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Method = request.Method.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            AuthTransaction = request.Headers.TryGetValues("X-TerraScale-Auth-Transaction", out var transactions) ? transactions.Single() : null;
            IfMatch = request.Headers.IfMatch.FirstOrDefault()?.ToString();
            var response = new HttpResponseMessage((HttpStatusCode)fixture.GetProperty("status").GetInt32());
            if (fixture.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
            {
                var media = fixture.GetProperty("mediaType").GetString()!;
                response.Content = new StringContent(media == "application/octet-stream" ? body.GetString()! : body.GetRawText(), Encoding.UTF8, media);
                if (media == "application/octet-stream")
                {
                    var headers = fixture.GetProperty("headers");
                    response.Headers.Add("X-TerraScale-Artifact-Length", headers.GetProperty("x-terrascale-artifact-length").GetString()!);
                    response.Headers.Add("X-TerraScale-Artifact-Checksum", headers.GetProperty("x-terrascale-artifact-checksum").GetString()!);
                }
            }
            return response;
        }
    }

    private sealed class ArtifactHandler(string length, string checksum, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            response.Headers.Add("X-TerraScale-Artifact-Length", length);
            response.Headers.Add("X-TerraScale-Artifact-Checksum", checksum);
            return Task.FromResult(response);
        }
    }
}
