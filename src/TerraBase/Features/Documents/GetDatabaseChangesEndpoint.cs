using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>GET …/databases/{database}/changes</c>: the database's changes feed as Server-Sent Events.</summary>
public sealed class GetDatabaseChangesEndpoint(IDatabaseChanges changes, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    private const int MaxCollections = 64;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.DatabaseRoute + "/changes");
        Name("streamDatabaseChanges");
        Summary("Streams the database's committed changes over SSE");
        Description("""
            Requires documents:read. Server-Sent Events: `change` events carry a DatabaseChangeEvent, and `cursor` events (when the stream starts and every 15 s) a DatabaseChangeCursor. Every event's id is the opaque cursor after it, valid on any machine of any region: per origin node, the highest stamp delivered. Each origin's events arrive in its commit order; events of different origins interleave. Delivery is at least once: an origin the cursor does not name is replayed from its first retained event, so deduplicate by (origin, version, collection, id). Only the last event of an (origin, version) carries an id. `document` is the document's current value on the serving machine, null when it no longer exists there. Query: cursor (a cursor, or `now` for the current head; the start of retention when absent), collections (comma-separated names, at most 64; a cursor is valid only with the same filter). Last-Event-ID resumes and takes precedence over cursor. 410 cursor_expired: retention (7 days) deleted an event the cursor still promised, or the cursor was issued more than 7 days ago; start again with cursor=now and re-read. 429 changes_busy: at most 32 streams per database per machine. A stream ends after 30 minutes or at the access token's expiry; reconnect with Last-Event-ID. Not in the feed: collection and index metadata, and the documents a dropped collection held.
            """);
        Tags("Documents");
        Produces<DatabaseChangeEvent>(StatusCodes.Status200OK, "text/event-stream");
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status410Gone);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        (DocumentRoute.CheckParameters(HttpContext, "cursor", "collections"), CheckCursor(HttpContext)) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (IQueryCollection query, StartAt start) => CheckCollections(query) switch
            {
                Validation error => ErrorResponse.From(error),
                Filter filter => await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
                {
                    Validation error => ErrorResponse.From(error),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    TenantContext database => await changes.StreamAsync(HttpContext, database, start.Cursor, start.Head, filter.Collections, cancellationToken),
                },
            },
        };

    // Last-Event-ID, else the cursor parameter: a cursor this API issued, `now`, or the start of retention when neither is
    // given (which, unlike an issued cursor, promises nothing about events retention already deleted).
    private static Valid<StartAt> CheckCursor(HttpContext context)
    {
        string? text = context.Request.Query.TryGetValue("cursor", out var parameter) ? parameter.ToString() : null;
        if (context.Request.Headers.TryGetValue("Last-Event-ID", out var resume))
        {
            if (resume.Count != 1)
            {
                return new Validation("Last-Event-ID must be one cursor", "invalid_cursor");
            }

            text = resume.ToString();
        }

        return text switch
        {
            null => new StartAt(null, Head: false),
            "now" => new StartAt(null, Head: true),
            _ => ChangeCursor.Decode(text) is { } cursor ? new StartAt(cursor, Head: false) : new Validation("the cursor is not one this API issued", "invalid_cursor"),
        };
    }

    private static Valid<Filter> CheckCollections(IQueryCollection query)
    {
        if (!query.TryGetValue("collections", out var raw))
        {
            return new Filter(null);
        }

        var names = raw.ToString().Split(',');
        if (names.Length > MaxCollections)
        {
            return new Validation($"collections names at most {MaxCollections} collections");
        }

        var collections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (DocumentRoute.CheckCollectionName(name) is Validation error)
            {
                return error;
            }

            collections.Add(name);
        }

        return new Filter(collections);
    }

    private sealed record StartAt(FeedCursor? Cursor, bool Head);

    private sealed record Filter(IReadOnlySet<string>? Collections);
}
