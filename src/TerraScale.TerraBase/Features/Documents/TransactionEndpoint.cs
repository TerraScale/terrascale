using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/transactions</c>: applies up to 1024 operations atomically.</summary>
public sealed class TransactionEndpoint(IDocumentService documents, IAuthorizationService authorization, ITenantDirectory tenants, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private const int MaxOps = 1024;
    private const int MaxAssertCounts = 16;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.DatabaseRoute + "/transactions");
        Name("runTransaction");
        Summary("Applies document operations atomically");
        Description("""
            {"ops":[…]} with 1 to 1024 operations across the collections of one database: create (put with if_none_match "*"; id is generated as a ULID when omitted), put (document, optional if_match or if_none_match "*"), patch (exactly one of merge or ops, optional if_match), delete (optional if_match), assert (filter: the document exists and matches; or absent: true) and assert_count (filter, gte and/or lte; at most 16 per transaction, at most 10000 documents examined). Assertions see the state before this transaction's writes. Two operations may not target the same document. Every failure names the failing operation in error.op. Assertions also need the documents:read scope. Query assertions and counts are interrupted at SQLite progress callbacks after 5 million VM instructions or a 5-second deadline (400 invalid_query, query too expensive). Caller-supplied document keys may be 1 to 256 characters without control characters. Generated ids are canonical uppercase ULIDs.
            Captured changes contain at most 2 MiB of UTF-8 collection names, ids and JSON. Exceeding this limit returns 503 capacity with the failing error.op, rolling back the entire transaction; split the writes into smaller transactions.
            """);
        Tags("Transactions");
        Metadata(new RequestBodyMetadata(typeof(TransactionRequest), DocumentRoute.JsonMediaType));
        Produces<TransactionBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckParameters(HttpContext), DocumentRoute.CheckIdempotencyKey(HttpContext)) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                _ => await DocumentRoute.CheckBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => await TransactAsync(database, body, cancellationToken),
                },
            },
        };

    private async ValueTask<IResult> TransactAsync(TenantContext database, byte[] body, CancellationToken cancellationToken)
    {
        // The envelope is read without parsing the operations, so each operation, duplicate members included,
        // is checked by RequestJson on its own and its failures carry its index.
        using var envelope = Envelope(body);
        if (envelope is null)
        {
            return ErrorResponse.From(new Validation("the body must be valid JSON"));
        }

        return Ops(envelope.RootElement) switch
        {
            Validation error => ErrorResponse.From(error),
            List<JsonElement> ops => await TransactAsync(database, ops, cancellationToken),
        };
    }

    private async ValueTask<IResult> TransactAsync(TenantContext database, List<JsonElement> ops, CancellationToken cancellationToken)
    {
        var operations = new List<DocumentOperation>(ops.Count);
        var targets = new HashSet<(string, string)>();
        var assertCounts = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            switch (CheckOperation(ops[i], time))
            {
                case Validation error:
                    return ErrorResponse.From(error, i);
                case DocumentOperation operation when operation is AssertCountOperation && ++assertCounts > MaxAssertCounts:
                    return ErrorResponse.From(new Validation("a transaction has at most 16 assert_count operations"), i);
                case DocumentOperation operation when Target(operation) is { } target && !targets.Add(target):
                    return ErrorResponse.From(new Validation("two operations target the same document"), i);
                case DocumentOperation operation:
                    operations.Add(operation);
                    break;
            }
        }

        var firstAssertion = operations.FindIndex(operation => operation is AssertOperation or AssertCountOperation);
        if (firstAssertion >= 0 && !(await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.DocumentsRead)).Succeeded)
        {
            // Like every operation-specific failure, it names the operation: the first assertion.
            return ErrorResponse.From(new Forbidden("assertions need the documents:read scope", "forbidden_scope"), firstAssertion);
        }

        return await documents.WriteAsync(database, operations, indexErrors: true, cancellationToken) switch
        {
            DocumentRejected rejected => ErrorResponse.From(rejected.Error, rejected.Op),
            WriteResult written => TypedResults.Ok(new TransactionBody(
                DocumentRoute.Decimal(written.Offset),
                DocumentRoute.Durability,
                [.. written.Results.Select(result => result switch
                {
                    { ETag: { } version } => new TransactionResultBody(result.Id, version, null, null),
                    { Count: { } count } => new TransactionResultBody(null, null, true, DocumentRoute.Decimal(count)),
                    _ => new TransactionResultBody(null, null, true, null),
                })])),
        };
    }

    /// <summary>The document an operation targets; <see langword="null"/> for <c>assert_count</c>.</summary>
    internal static (string, string)? Target(DocumentOperation operation) => operation switch
    {
        PutOperation put => (put.Collection, put.Id),
        PatchOperation patch => (patch.Collection, patch.Id),
        DeleteOperation delete => (delete.Collection, delete.Id),
        AssertOperation assert => (assert.Collection, assert.Id),
        _ => null,
    };

    /// <summary>Parses and validates one operation of a transaction (also of a Management commit), or answers the rule it breaks.</summary>
    internal static Valid<DocumentOperation> CheckOperation(JsonElement json, TimeProvider time)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new Validation("each operation is a JSON object");
        }

        // Optional operation members may be omitted, but explicit null is never an omission.
        if (json.EnumerateObject().Any(member => member.Value.ValueKind == JsonValueKind.Null))
        {
            return new Validation("operation members cannot be null");
        }

        return RequestJson.Read<TransactionOpRequest>(JsonMarshal.GetRawUtf8Value(json), "invalid_request") switch
        {
            Validation error => error,
            TransactionOpRequest op => DocumentRoute.CheckCollectionName(op.Collection) switch
            {
                Validation error => error,
                string collection => Operation(op, collection, time),
            },
        };
    }

    private static Valid<DocumentOperation> Operation(TransactionOpRequest op, string collection, TimeProvider time)
    {
        var hasDocument = op.Document.ValueKind != JsonValueKind.Undefined;
        var hasMerge = op.Merge.ValueKind != JsonValueKind.Undefined;
        var hasFilter = op.Filter.ValueKind != JsonValueKind.Undefined;
        var hasPatch = hasMerge || op.Ops is not null;
        var hasAssert = hasFilter || op.Absent is not null || op.Gte is not null || op.Lte is not null;
        var hasPrecondition = op.IfMatch is not null || op.IfNoneMatch is not null;
        var isAssert = op.Op == "assert" && hasFilter != (op.Absent == true) && op.Absent != false && op.Gte is null && op.Lte is null
            && !hasDocument && !hasPatch && !hasPrecondition;
        var isAssertCount = op.Op == "assert_count" && op.Id is null && (op.Gte is not null || op.Lte is not null) && (op.Gte is null or >= 0)
            && (op.Lte is null or >= 0) && !(op.Gte > op.Lte) && op.Absent is null && !hasDocument && !hasPatch && !hasPrecondition;
        return op.Op switch
        {
            "create" when hasDocument && !hasPatch && !hasAssert && !hasPrecondition =>
                (op.Id is null ? EntityIds.New(time).ToString() : DocumentRoute.CheckDocumentId(op.Id)) switch
                {
                    Validation error => error,
                    string id => (DocumentOperation)new PutOperation(collection, id, Document(op.Document), Documents.Precondition.Absent),
                },
            "put" when hasDocument && !hasPatch && !hasAssert => (DocumentRoute.CheckDocumentId(op.Id), Precondition(op)) switch
            {
                (Validation error, _) => error,
                (_, Validation error) => error,
                (string id, Precondition precondition) => (DocumentOperation)new PutOperation(collection, id, Document(op.Document), precondition),
            },
            "patch" when hasMerge != (op.Ops is not null) && !hasDocument && !hasAssert && op.IfNoneMatch is null =>
                (DocumentRoute.CheckDocumentId(op.Id), hasMerge ? DocumentPatch.CheckMerge(op.Merge) : DocumentPatch.CheckOps(op.Ops), Precondition(op)) switch
                {
                    (Validation error, _, _) => error,
                    (_, Validation error, _) => error,
                    (_, _, Validation error) => error,
                    (string id, DocumentPatch patch, Precondition precondition) => (DocumentOperation)new PatchOperation(collection, id, patch, precondition),
                },
            "delete" when !hasDocument && !hasPatch && !hasAssert && op.IfNoneMatch is null => (DocumentRoute.CheckDocumentId(op.Id), Precondition(op)) switch
            {
                (Validation error, _) => error,
                (_, Validation error) => error,
                (string id, Precondition precondition) => (DocumentOperation)new DeleteOperation(collection, id, precondition),
            },
            "assert" when isAssert && hasFilter => (DocumentRoute.CheckDocumentId(op.Id), QueryParser.CheckFilter(op.Filter, "invalid_request")) switch
            {
                (Validation error, _) => error,
                (_, Validation error) => error,
                (string id, DocumentFilter filter) => (DocumentOperation)new AssertOperation(collection, id, filter),
            },
            "assert" when isAssert => DocumentRoute.CheckDocumentId(op.Id) switch
            {
                Validation error => error,
                string id => (DocumentOperation)new AssertOperation(collection, id, null),
            },
            "assert_count" when isAssertCount && hasFilter => QueryParser.CheckFilter(op.Filter, "invalid_request") switch
            {
                Validation error => error,
                DocumentFilter filter => (DocumentOperation)new AssertCountOperation(collection, filter, op.Gte, op.Lte),
            },
            "assert_count" when isAssertCount => (DocumentOperation)new AssertCountOperation(collection, null, op.Gte, op.Lte),
            _ => new Validation("the operation's kind or members are invalid"),
        };
    }

    // if_match is read like If-Match (compared strongly); if_none_match is only "*".
    private static Valid<Precondition> Precondition(TransactionOpRequest op) => op.IfNoneMatch switch
    {
        null => op.IfMatch is null ? new Precondition(null, IfMatchAny: false, IfNoneMatch: false) : ETags.CheckIfMatch(op.IfMatch),
        "*" when op.IfMatch is null => Documents.Precondition.Absent,
        _ => new Validation("if_none_match is \"*\" and excludes if_match"),
    };

    // Null when the body is not valid JSON.
    private static JsonDocument? Envelope(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // {"ops":[…]} exactly: one ops member holding 1 to 1024 operations.
    private static Valid<List<JsonElement>> Ops(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return new Validation("the body must be a JSON object");
        }

        if (envelope.EnumerateObject().ToList() is not [{ Name: "ops", Value.ValueKind: JsonValueKind.Array } ops])
        {
            return new Validation("the body is {\"ops\":[…]} with no other or repeated member");
        }

        return ops.Value.GetArrayLength() is > 0 and <= MaxOps
            ? new List<JsonElement>(ops.Value.EnumerateArray())
            : new Validation("ops must hold 1 to 1024 operations");
    }

    private static byte[] Document(JsonElement document)
    {
        var json = JsonMarshal.GetRawUtf8Value(document).ToArray();
        return json;
    }
}
