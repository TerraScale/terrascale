using System.Net;
using System.Text;
using TUnit.Assertions.Enums;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    public sealed class DocumentChangesContractTests
    {
        private const string First = "AQAAAAAAAAABAAAAAAAAAAE";
        private const string Next = "AQAAAAAAAAABAAAAAAAAAAI";

        [Test]
        public async Task ResumeKeepsCollectionAndHeartbeatCheckpointWhileDuplicateChangesAdvanceCursor()
        {
            using var wire = new Wire();
            var updates = new List<DocumentChangesUpdate>();
            await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", "now", First))
            {
                updates.Add(update);
            }

            await Assert.That(wire.LastEventId).IsEqualTo(First);
            await Assert.That(wire.Query).IsEqualTo("?collections=items");
            await Assert.That(updates.Select(update => update.State)).IsEquivalentTo(new[] { "change", "cursor", "cursor", "reconnect" }, CollectionOrdering.Matching);
            await Assert.That(updates.Last().Cursor).IsEqualTo(Next);
            await Assert.That(updates.Single(update => update.State == "change").Change!.Document).IsNull();
            await Assert.That(updates.Single(update => update.State == "change").Change!.Op).IsEqualTo("upsert");
            await Assert.That(wire.Content!.Disposed).IsTrue();
            await Assert.That(wire.StreamRequests).IsEqualTo(1);
        }

        [Test]
        public async Task ExpiredCheckpointRequestsExplicitResyncWithoutResettingOrRetrying()
        {
            using var wire = new Wire { Expired = true };
            var updates = new List<DocumentChangesUpdate>();
            await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", First))
            {
                updates.Add(update);
            }

            await Assert.That(updates.Count).IsEqualTo(1);
            await Assert.That(updates[0].State).IsEqualTo("resync_required");
            await Assert.That(updates[0].Cursor).IsNull();
            await Assert.That(wire.StreamRequests).IsEqualTo(1);
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        public async Task InitialCheckpointlessChangeIsPresentedWithoutInventingAResumeId()
        {
            using var wire = new Wire { Checkpointless = true };
            await using (var stream = Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", "now").GetAsyncEnumerator())
            {
                await Assert.That(await stream.MoveNextAsync()).IsTrue();
                await Assert.That(stream.Current.State).IsEqualTo("change");
                await Assert.That(stream.Current.Cursor).IsNull();
                await Assert.That(stream.Current.Change!.Id).IsEqualTo("txn");
                await Assert.That(await stream.MoveNextAsync()).IsTrue();
                await Assert.That(stream.Current.Cursor).IsEqualTo(First);
            }
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        [Arguments(false)]
        [Arguments(true)]
        public async Task TransactionChangesSharingVersionRetainPriorCheckpointUntilFinalChange(bool hasCheckpoint)
        {
            var change = "{\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}";
            var second = change.Replace("\"a\"", "\"b\"", StringComparison.Ordinal);
            using var wire = new Wire
            {
                Payload = $"event: change\ndata: {change}\n\nevent: change\nid: {Next}\ndata: {second}\n\n"
            };
            var updates = new List<DocumentChangesUpdate>();
            await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", hasCheckpoint ? First : "now"))
            {
                updates.Add(update);
            }

            await Assert.That(updates.Select(update => update.State)).IsEquivalentTo(new[] { "change", "change", "reconnect" }, CollectionOrdering.Matching);
            await Assert.That(updates[0].Cursor).IsEqualTo(hasCheckpoint ? First : null);
            await Assert.That(updates[1].Cursor).IsEqualTo(Next);
            await Assert.That(updates[0].Change!.Id).IsEqualTo("a");
            await Assert.That(updates[1].Change!.Id).IsEqualTo("b");
            await Assert.That(updates[2].Cursor).IsEqualTo(Next);
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        public async Task HalfTransactionDisposalKeepsAcceptedCheckpointAndPermitsReplay()
        {
            var change = "{\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}";
            var second = change.Replace("\"a\"", "\"b\"", StringComparison.Ordinal);
            var payload = $"event: change\ndata: {change}\n\nevent: change\nid: {Next}\ndata: {second}\n\n";
            using var wire = new Wire { Payload = payload };
            await using (var stream = Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", First).GetAsyncEnumerator())
            {
                await Assert.That(await stream.MoveNextAsync()).IsTrue();
                await Assert.That(stream.Current.Cursor).IsEqualTo(First);
                await Assert.That(stream.Current.Change!.Id).IsEqualTo("a");
            }
            await Assert.That(wire.Content!.Disposed).IsTrue();
            var replay = new List<DocumentChangesUpdate>();
            await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", First))
            {
                replay.Add(update);
            }

            await Assert.That(replay.Where(update => update.State == "change").Select(update => update.Change!.Id))
                .IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
            await Assert.That(replay.Last().Cursor).IsEqualTo(Next);
            await Assert.That(wire.StreamRequests).IsEqualTo(2);
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        [Arguments(false)]
        [Arguments(true)]
        public async Task HalfTransactionCancellationPreservesCheckpointAndReplaysEveryDocument(bool hasCheckpoint)
        {
            var change = "{\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}";
            var second = change.Replace("\"a\"", "\"b\"", StringComparison.Ordinal);
            using var wire = new Wire { Payload = $"event: change\ndata: {change}\n\nevent: change\nid: {Next}\ndata: {second}\n\n" };
            using var cancellation = new CancellationTokenSource();
            string? checkpoint;
            await using (var stream = Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", hasCheckpoint ? First : "now",
                cancellationToken: cancellation.Token).GetAsyncEnumerator())
            {
                await Assert.That(await stream.MoveNextAsync()).IsTrue();
                await Assert.That(stream.Current.Change!.Id).IsEqualTo("a");
                checkpoint = stream.Current.Cursor;
                await Assert.That(checkpoint).IsEqualTo(hasCheckpoint ? First : null);
                await cancellation.CancelAsync();
                OperationCanceledException? canceled = null;
                try { await stream.MoveNextAsync(); }
                catch (OperationCanceledException error) { canceled = error; }
                await Assert.That(canceled).IsNotNull();
            }
            await Assert.That(wire.Content!.Disposed).IsTrue();
            var replay = new List<DocumentChangesUpdate>();
            await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", checkpoint))
            {
                replay.Add(update);
            }

            await Assert.That(wire.Query).IsEqualTo(checkpoint is null ? "?collections=items" : "?cursor=" + First + "&collections=items");
            await Assert.That(replay.Where(update => update.State == "change").Select(update => update.Change!.Id))
                .IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
            await Assert.That(replay.Last().Cursor).IsEqualTo(Next);
            await Assert.That(wire.StreamRequests).IsEqualTo(2);
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        public async Task ForeignCollectionFailsBeforePresentingItsChangeOrCheckpoint()
        {
            using var wire = new Wire
            {
                Payload = $"event: change\nid: {Next}\ndata: {{\"collection\":\"foreign\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}}\n\n"
            };
            var updates = new List<DocumentChangesUpdate>();
            HttpRequestException? refused = null;
            try
            {
                await foreach (var update in Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", First))
                {
                    updates.Add(update);
                }
            }
            catch (HttpRequestException error) { refused = error; }
            await Assert.That(refused).IsNotNull();
            await Assert.That(updates.Count).IsEqualTo(0);
            await Assert.That(wire.StreamRequests).IsEqualTo(1);
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        [Test]
        public async Task PartialEnumerationDisposesConnection()
        {
            using var wire = new Wire();
            await using (var stream = Service(wire).ReadAsync(Context(), "01JA0000000000000000000004", "items", "now").GetAsyncEnumerator())
            {
                await Assert.That(await stream.MoveNextAsync()).IsTrue();
            }
            await Assert.That(wire.Content!.Disposed).IsTrue();
        }

        private static DocumentChangesService Service(Wire wire)
        {
            var tokens = new Tokens();
            var databases = new DatabaseService(null!, new DatabaseBackend(wire, tokens));
            return new(databases, tokens, wire);
        }
        private static AccountContext Context()
        {
            var project = new ProjectRecord { Id = "01JA0000000000000000000001", OrganizationId = "01JA0000000000000000000002", Name = "project", Region = "gru" };
            var database = new DatabaseRecord { Id = "01JA0000000000000000000004", ProjectId = project.Id, Name = "app", Region = "gru" };
            project.Databases.Add(database.Id, database);
            var organization = new OrganizationRecord { Id = project.OrganizationId, Name = "org" };
            organization.Projects.Add(project.Id, project);
            return new(new("user", "Synthetic", "synthetic@example.test", "original", null, DateTimeOffset.UtcNow.AddMinutes(5)),
                new() { UserId = "user" }, [organization], organization, project);
        }
        private sealed class Tokens : IProjectAccessTokens
        {
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                if (scopes.Single() != "documents:read")
                {
                    throw new InvalidOperationException("Wrong stream authority.");
                }

                return Task.FromResult("read-project-token");
            }
        }
        private sealed class TrackedContent(string value, string mediaType) : StringContent(value, Encoding.UTF8, mediaType)
        {
            public bool Disposed { get; private set; }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public bool Expired { get; init; }
            public bool Checkpointless { get; init; }
            public string? Payload { get; init; }
            public int StreamRequests { get; private set; }
            public string? LastEventId { get; private set; }
            public string? Query { get; private set; }
            public TrackedContent? Content { get; private set; }
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath == "/v1/regions")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"regions\":[\"gru\"]}", Encoding.UTF8, "application/json") });
                }

                if (request.RequestUri!.AbsolutePath == "/healthz")
                {
                    var health = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"region\":\"gru\"}", Encoding.UTF8, "application/json") };
                    health.Headers.Add("X-TerraBase-Region", "gru");
                    return Task.FromResult(health);
                }
                if (request.RequestUri.AbsolutePath != "/v1/projects/01JA0000000000000000000001/databases/app/changes" ||
                    request.Headers.Authorization?.Parameter != "read-project-token" || request.Headers.GetValues("X-TerraScale-Region-Pin").Single() != "gru")
                {
                    throw new InvalidOperationException("Unexpected stream contract.");
                }

                StreamRequests++;
                Query = request.RequestUri.Query;
                LastEventId = request.Headers.TryGetValues("Last-Event-ID", out var checkpoint) ? checkpoint.Single() : null;
                var change = "{\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}";
                var initial = Checkpointless ? "event: change\ndata: " + change.Replace("\"a\"", "\"txn\"", StringComparison.Ordinal) + "\n\n" : "";
                Content = Expired ? new("{\"error\":{\"code\":\"cursor_expired\",\"message\":\"expired\"}}", "application/json") :
                    new(Payload ?? initial + $"event: change\nid: {First}\ndata: {change}\n\nevent: change\nid: {Next}\ndata: {change}\n\nevent: cursor\nid: {Next}\ndata: {{\"cursor\":\"{Next}\"}}\n\n", "text/event-stream");
                var response = new HttpResponseMessage(Expired ? HttpStatusCode.Gone : HttpStatusCode.OK) { Content = Content };
                response.Headers.Add("X-TerraBase-Region", "gru");
                return Task.FromResult(response);
            }
        }
    }
}
