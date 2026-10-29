using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    public sealed class DocumentBackendContractTests
    {
        [Test]
        public async Task EachReadExchangesCurrentAuthorityWithoutIssuingADataKey()
        {
            using var wire = new Wire();
            var tokens = new Tokens();
            var backend = new DocumentBackend(wire, tokens);
            for (var index = 0; index < 2; index++)
            {
                var document = await backend.GetAsync(Scope(), DatabaseRegion.FromId("gru")!, "app", "items", "01K78D0C000000000000000099", CancellationToken.None);
                await Assert.That(document).IsNull();
            }
            await Assert.That(tokens.Scopes).IsEquivalentTo(new[] { "documents:read", "documents:read" });
            await Assert.That(wire.Requests).IsEqualTo(2);
            await Assert.That(wire.Bearers).IsEquivalentTo(new[] { "project-1", "project-2" });
            await Assert.That(wire.Path).IsEqualTo("/v1/projects/01JA0000000000000000000001/databases/app/collections/items/documents/by-key");
            await Assert.That(wire.Query).IsEqualTo("?id=01K78D0C000000000000000099");
            await Assert.That(wire.Pin).IsEqualTo("gru");
        }

        [Test]
        [Arguments(503)]
        [Arguments(0)]
        public async Task AmbiguousCreateKeepsItsGeneratedIdAndNeverReplays(int failure)
        {
            using var wire = new Wire { Failure = failure };
            var tokens = new Tokens();
            var backend = new DocumentBackend(wire, tokens);
            DocumentWriteOutcomeUnknownException? unknown = null;
            using var body = JsonDocument.Parse("{\"title\":\"kept\"}");
            try { await backend.WriteAsync(Scope(), DatabaseRegion.FromId("gru")!, "app", "items", "create", "", body.RootElement, "", CancellationToken.None); }
            catch (DocumentWriteOutcomeUnknownException error) { unknown = error; }
            await Assert.That(unknown).IsNotNull();
            await Assert.That(TerraScaleIds.IsValid(unknown!.OperationId)).IsTrue();
            await Assert.That(TerraScaleIds.IsValid(unknown.DocumentId)).IsTrue();
            await Assert.That(wire.Path).EndsWith("/documents/by-key");
            await Assert.That(wire.Query).IsEqualTo("?id=" + unknown.DocumentId);
            await Assert.That(wire.Method).IsEqualTo(HttpMethod.Put);
            await Assert.That(wire.IfNoneMatch).IsEqualTo("*");
            await Assert.That(wire.IdempotencyKey).IsNull();
            await Assert.That(wire.Requests).IsEqualTo(1);
            await Assert.That(wire.Body).IsEqualTo("{\"title\":\"kept\"}");
            await Assert.That(tokens.Scopes.Single()).IsEqualTo("documents:write");
        }

        [Test]
        [Arguments("collections", "documents:read")]
        [Arguments("create-collection", "documents:write")]
        [Arguments("query", "documents:read")]
        [Arguments("list", "documents:read")]
        [Arguments("get", "documents:read")]
        [Arguments("schema", "databases:read")]
        [Arguments("create", "documents:write")]
        [Arguments("replace", "documents:write")]
        [Arguments("patch", "documents:write")]
        [Arguments("delete", "documents:write")]
        public async Task EveryActionPreservesExchangeRefusalsWithOnlyItsProducerRequiredScope(string action, string permission)
        {
            using var body = JsonDocument.Parse("{\"title\":\"kept\"}");
            foreach (var status in new[] { 401, 403, 404, 429, 503, 0 })
            {
                using var wire = new Wire();
                var tokens = new Tokens { Refusal = status };
                var backend = new DocumentBackend(wire, tokens);
                var scope = Scope();
                var region = DatabaseRegion.FromId("gru")!;
                HttpRequestException? denied = null;
                try
                {
                    Task request = action switch
                    {
                        "collections" => backend.CollectionsAsync(scope, region, "app", CancellationToken.None),
                        "create-collection" => backend.CreateCollectionAsync(scope, region, "app", "items", CancellationToken.None),
                        "query" => backend.DocumentsAsync(scope, region, "app", "items", new TerraBaseQuery(), CancellationToken.None),
                        "list" => backend.ListAsync(scope, region, "app", "items", 10, null, CancellationToken.None),
                        "get" => backend.GetAsync(scope, region, "app", "items", "01K78D0C000000000000000099", CancellationToken.None),
                        "schema" => backend.SchemaAsync(scope, region, "app", "items", CancellationToken.None),
                        _ => backend.WriteAsync(scope, region, "app", "items", action, DocumentId, body.RootElement, "", CancellationToken.None)
                    };
                    await request;
                }
                catch (HttpRequestException error) { denied = error; }
                await Assert.That(denied).IsNotNull();
                await Assert.That(denied is DocumentWriteOutcomeUnknownException).IsFalse();
                await Assert.That(denied!.StatusCode).IsEqualTo(status == 0 ? (HttpStatusCode?)null : (HttpStatusCode)status);
                await Assert.That(tokens.Scopes.Single()).IsEqualTo(permission);
                await Assert.That(wire.Requests).IsEqualTo(0);
            }
        }

        [Test]
        [Arguments("malformed")]
        [Arguments("missing")]
        [Arguments("foreign")]
        [Arguments("valid")]
        public async Task OnlyACompleteMatchingAcknowledgementConfirmsTheWrite(string receipt)
        {
            using var wire = new Wire { Receipt = receipt };
            var backend = new DocumentBackend(wire, new Tokens());
            using var body = JsonDocument.Parse("{\"title\":\"kept\"}");
            DocumentWriteOutcomeUnknownException? unknown = null;
            TerraBaseWriteResult? result = null;
            try { result = await backend.WriteAsync(Scope(), DatabaseRegion.FromId("gru")!, "app", "items", "replace", DocumentId, body.RootElement, "", CancellationToken.None); }
            catch (DocumentWriteOutcomeUnknownException error) { unknown = error; }
            await Assert.That(wire.Requests).IsEqualTo(1);
            if (receipt == "valid")
            {
                await Assert.That(unknown).IsNull();
                await Assert.That(result!.Id).IsEqualTo(DocumentId);
            }
            else
            {
                await Assert.That(result).IsNull();
                await Assert.That(unknown).IsNotNull();
                await Assert.That(unknown!.DocumentId).IsEqualTo(DocumentId);
                await Assert.That(TerraScaleIds.IsValid(unknown.OperationId)).IsTrue();
            }
        }

        [Test]
        [Arguments(403)]
        [Arguments(412)]
        public async Task KnownWriteRejectionKeepsItsStatusAndDoesNotReplay(int status)
        {
            using var wire = new Wire { Failure = status };
            using var body = JsonDocument.Parse("{\"title\":\"kept\"}");
            HttpRequestException? rejected = null;
            try { await new DocumentBackend(wire, new Tokens()).WriteAsync(Scope(), DatabaseRegion.FromId("gru")!, "app", "items", "replace", DocumentId, body.RootElement, "", CancellationToken.None); }
            catch (HttpRequestException error) { rejected = error; }
            await Assert.That(rejected?.StatusCode).IsEqualTo((HttpStatusCode)status);
            await Assert.That(wire.Requests).IsEqualTo(1);
        }

        private const string DocumentId = "01K78000000000000000000001";
        private static DatabaseScope Scope()
        {
            return new("user", "original-user-token", "01JA0000000000000000000002", "org", "01JA0000000000000000000001", "project")
            { Session = new("user", "Synthetic", "synthetic@example.test", "original-user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)) };
        }

        private sealed class Tokens : IProjectAccessTokens
        {
            public int? Refusal { get; init; }
            public List<string> Scopes { get; } = [];
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                if (session.AccessToken != "original-user-token" || organizationId != "01JA0000000000000000000002" || projectId != "01JA0000000000000000000001")
                {
                    throw new InvalidOperationException("Authority changed.");
                }

                Scopes.Add(scopes.Single());
                if (Refusal is { } status)
                {
                    throw new HttpRequestException("Exchange refused", null, status == 0 ? null : (HttpStatusCode)status);
                }

                return Task.FromResult("project-" + Scopes.Count);
            }
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public int Failure { get; init; } = -1;
            public string Receipt { get; init; } = "";
            public int Requests { get; private set; }
            public List<string> Bearers { get; } = [];
            public string Path { get; private set; } = "";
            public string Query { get; private set; } = "";
            public string? Pin { get; private set; }
            public string? IfNoneMatch { get; private set; }
            public string? IdempotencyKey { get; private set; }
            public HttpMethod? Method { get; private set; }
            public string? Body { get; private set; }
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                Path = request.RequestUri!.AbsolutePath;
                Query = request.RequestUri.Query;
                Method = request.Method;
                Pin = request.Headers.GetValues("X-TerraScale-Region-Pin").Single();
                Bearers.Add(request.Headers.Authorization!.Parameter!);
                IfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
                IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (Receipt.Length > 0)
                {
                    var receipt = Receipt switch
                    {
                        "malformed" => "{",
                        "missing" => "{}",
                        _ => JsonSerializer.Serialize(new { id = Receipt == "foreign" ? "01K78000000000000000000009" : DocumentId, version = "v2", offset = 2L, durability = "local_durable" })
                    };
                    return new(HttpStatusCode.OK) { Content = new StringContent(receipt, Encoding.UTF8, "application/json") };
                }
                if (Failure == 0)
                {
                    throw new HttpRequestException("Connection reset after send.");
                }

                if (Failure > 0)
                {
                    return new((HttpStatusCode)Failure) { Content = new StringContent("{\"error\":\"unavailable\"}", Encoding.UTF8, "application/json") };
                }

                return new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"not_found\",\"message\":\"Document not found.\"}}", Encoding.UTF8, "application/json") };
            }
        }
    }
}
