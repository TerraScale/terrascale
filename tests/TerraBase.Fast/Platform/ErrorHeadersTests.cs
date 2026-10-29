using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Platform;

public sealed class ErrorHeadersTests
{
    [Test]
    public async Task Independently_cancelled_work_returns_a_structured_uncached_error()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IDocumentService>(provider => new CancelledCollections(provider.GetRequiredService<DocumentService>())));
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var response = await reader.GetAsync(new Uri($"{api.Collections}/{DocumentApi.Collection}", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
    }

    [Test]
    public async Task Status_code_pages_are_never_cached_and_preserve_status_headers()
    {
        await using var api = await DocumentApi.StartAsync();
        var server = api.Server;
        using var anonymous = api.Management.DataClient();
        using var writer = await api.ClientWithScopesAsync("documents:write");
        var collection = new Uri($"{api.Collections}/{DocumentApi.Collection}", UriKind.Relative);
        using var unauthenticated = await anonymous.GetAsync(collection);
        using var forbidden = await writer.GetAsync(collection);
        using var unknown = await server.Client.GetAsync(new Uri("/no-such-route", UriKind.Relative));
        using var wrongMethod = await server.Client.PostAsync(new Uri("/healthz", UriKind.Relative), null);

        foreach (var (response, status, code) in new[]
        {
            (unauthenticated, HttpStatusCode.Unauthorized, "invalid_key"),
            (forbidden, HttpStatusCode.Forbidden, "forbidden_scope"),
            (unknown, HttpStatusCode.NotFound, "unknown_route"),
            (wrongMethod, HttpStatusCode.MethodNotAllowed, "method_not_allowed"),
        })
        {
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        }

        await Assert.That(wrongMethod.Content.Headers.Allow).Contains("GET");
    }

    private sealed class CancelledCollections(IDocumentService inner) : IDocumentService
    {
        public ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromCanceled<CollectionResult>(new CancellationToken(canceled: true));
        }

        public ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.CreateCollectionAsync(database, name, cancellationToken);

        public ValueTask<Found<CollectionPage>> ListCollectionsAsync(TenantContext database, string? afterName, int limit, CancellationToken cancellationToken) =>
            inner.ListCollectionsAsync(database, afterName, limit, cancellationToken);

        public ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.DropCollectionAsync(database, name, cancellationToken);

        public ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

        public void AcceptRead(DocumentKey key, StoredDocument document) => inner.AcceptRead(key, document);

        public void AcceptRead(TenantContext database, string collection, QueryPage page) => inner.AcceptRead(database, collection, page);

        public ValueTask<QueryResult> QueryAsync(TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken) =>
            inner.QueryAsync(database, query, countLimit, cancellationToken);

        public ValueTask<WriteOutcome> WriteAsync(
            TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken) =>
            inner.WriteAsync(database, operations, indexErrors, cancellationToken);

        public ValueTask<WriteOutcome> WriteConfirmedAsync(
            TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken) =>
            inner.WriteConfirmedAsync(database, operations, indexErrors, cancellationToken);

        public IAsyncEnumerable<ExportRow> ExportAsync(TenantContext database, string? afterCollection, string? afterId, CancellationToken cancellationToken) =>
            inner.ExportAsync(database, afterCollection, afterId, cancellationToken);

        public ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken) =>
            inner.ApplyAsync(transaction, operation, cancellationToken);

        public ValueTask<QueryResult> QueryAsync(
            IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null) =>
            inner.QueryAsync(transaction, query, countLimit, cancellationToken, observation);

        public ValueTask<CollectionRulesResult> GetCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionRulesAsync(database, name, cancellationToken);
        public ValueTask<CollectionRulesResult> SetCollectionRulesAsync(TenantContext database, string name, CollectionAccessRules rules, CancellationToken cancellationToken) =>
            inner.SetCollectionRulesAsync(database, name, rules, cancellationToken);

        public ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionSchemaAsync(database, name, cancellationToken);

        public ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken) =>
            inner.SetCollectionSchemaAsync(database, name, schema, cancellationToken);

        public ValueTask<CollectionSchemaPreviewResult> PreviewCollectionSchemaAsync(TenantContext database, string name, JsonElement schema, string? cursor, CancellationToken cancellationToken) =>
            inner.PreviewCollectionSchemaAsync(database, name, schema, cursor, cancellationToken);

        public ValueTask<CollectionTtlResult> GetCollectionTtlAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionTtlAsync(database, name, cancellationToken);

        public ValueTask<CollectionTtlResult> SetCollectionTtlAsync(TenantContext database, string name, string? path, CancellationToken cancellationToken) =>
            inner.SetCollectionTtlAsync(database, name, path, cancellationToken);
    }
}
