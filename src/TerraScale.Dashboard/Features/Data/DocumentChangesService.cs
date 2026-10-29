using System.Net;
using System.Runtime.CompilerServices;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed record DocumentChangesUpdate(string State, string? Cursor, TerraBaseChange? Change = null, string? Region = null);

// One SDK connection per request. The consumer owns reconnects and keeps the collection filter unchanged.
public sealed class DocumentChangesService(DatabaseService databases, IProjectAccessTokens tokens, IHttpClientFactory http)
{
    public async IAsyncEnumerable<DocumentChangesUpdate> ReadAsync(AccountContext context, string databaseId, string collection,
        string? cursor = null, string? lastEventId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        collection = DataService.ValidCollection(collection);
        var database = DatabaseService.Database(context, databaseId);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var scope = DatabaseService.Scope(context);
        var token = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, ["documents:read"], cancellationToken);
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = _ => ValueTask.FromResult(token)
        });
        var seen = new HashSet<(string Origin, string Version, string Collection, string Id)>();
        var order = new Queue<(string Origin, string Version, string Collection, string Id)>();
        var checkpoint = lastEventId ?? (cursor == "now" ? null : cursor);
        await using var stream = client.Database(scope.ProjectId, database.Name)
            .StreamChangesAsync(cursor, lastEventId, [collection], cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var expired = false;
            bool hasNext;
            try { hasNext = await stream.MoveNextAsync(); }
            catch (TerraBaseException error) when (error.StatusCode == HttpStatusCode.Gone && error.ErrorCode == "cursor_expired")
            { hasNext = false; expired = true; }
            if (expired)
            {
                yield return new("resync_required", null);
                yield break;
            }
            if (!hasNext)
            {
                yield return new("reconnect", checkpoint);
                yield break;
            }
            var item = stream.Current;
            var nextCheckpoint = item.Cursor ?? checkpoint;
            if (item.Change is not { } change)
            {
                yield return new("cursor", nextCheckpoint, Region: item.Region);
                checkpoint = nextCheckpoint;
                continue;
            }
            if (change.Collection != collection) throw new HttpRequestException("The changes feed returned a foreign collection.");
            var identity = (change.Origin, change.Version, change.Collection, change.Id);
            if (!seen.Add(identity))
            {
                yield return new("cursor", nextCheckpoint, Region: item.Region);
                checkpoint = nextCheckpoint;
                continue;
            }
            order.Enqueue(identity);
            if (order.Count > 1024) seen.Remove(order.Dequeue());
            yield return new("change", nextCheckpoint, change, item.Region);
            checkpoint = nextCheckpoint;
        }
    }
}
