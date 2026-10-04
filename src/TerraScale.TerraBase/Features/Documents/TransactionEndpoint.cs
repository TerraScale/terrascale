using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>POST /v1/projects/{project}/databases/{database}/transactions</c>: applies up to 1024 operations atomically.</summary>
public sealed class TransactionEndpoint(IDocumentService documents, IAuthorizationService authorization, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    private const int MaxOps = 1024;
    private const int MaxAssertCounts = 16;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(DocumentRoute.DatabaseRoute + "/transactions");
        Name("transaction");
        Summary("Applies document operations atomically");
        Description("""
            {"ops":[…]} with 1 to 1024 operations across the collections of one database: create (put with if_none_match "*"; id is generated as a UUIDv7 when omitted), put (document, optional if_match or if_none_match "*"), patch (exactly one of merge or ops, optional if_match), delete (optional if_match), assert (filter: the document exists and matches; or absent: true) and assert_count (filter, gte and/or lte; at most 16 per transaction, at most 10000 documents examined). Assertions see the state before this transaction's writes. Two operations may not target the same document. Every failure names the failing operation in error.op. Assertions also need the documents:read scope. Query assertions and counts are interrupted at SQLite progress callbacks after 5 million VM instructions or a 5-second deadline (400 invalid_query, query too expensive). Existing non-UUID legacy document ids remain addressable; new ids must be lowercase hyphenated UUIDs.
            """);
        Tags("Documents");
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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken);
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        var body = await DocumentRoute.ReadBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType);
        // The envelope is read without parsing the operations, so each operation, duplicate members included,
        // is checked by RequestJson on its own and its failures carry its index.
        using var envelope = Envelope(body);
        var ops = Ops(envelope.RootElement);

        var operations = new List<DocumentOperation>(ops.Count);
        var targets = new HashSet<(string, string)>();
        var assertCounts = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            try
            {
                var operation = Operation(ops[i]);
                if (operation is AssertCountOperation && ++assertCounts > MaxAssertCounts)
                {
                    throw new ApiException("a transaction has at most 16 assert_count operations");
                }

                if (Target(operation) is { } target && !targets.Add(target))
                {
                    throw new ApiException("two operations target the same document");
                }

                operations.Add(operation);
            }
            catch (ApiException error)
            {
                error.Op = i;
                throw;
            }
        }

        if (operations.Any(operation => operation is AssertOperation or AssertCountOperation)
            && !(await authorization.AuthorizeAsync(HttpContext.User, ApiKeyScopes.DocumentsRead)).Succeeded)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden_scope", "assertions need the documents:read scope");
        }

        var written = await documents.WriteAsync(database, operations, indexErrors: true, cancellationToken);
        return TypedResults.Ok(new TransactionBody(
            DocumentRoute.Decimal(written.Offset),
            DocumentRoute.Durability,
            [.. operations.Zip(written.Results, (operation, result) => result switch
            {
                { Version: { } version } => new TransactionResultBody(result.Id, ETags.Version(written.Incarnation, operation.Collection, result.Id!, version), null, null),
                { Count: { } count } => new TransactionResultBody(null, null, true, DocumentRoute.Decimal(count)),
                _ => new TransactionResultBody(null, null, true, null),
            })]));
    }

    private static (string, string)? Target(DocumentOperation operation) => operation switch
    {
        PutOperation put => (put.Collection, put.Id),
        PatchOperation patch => (patch.Collection, patch.Id),
        DeleteOperation delete => (delete.Collection, delete.Id),
        AssertOperation assert => (assert.Collection, assert.Id),
        _ => null,
    };

    private static DocumentOperation Operation(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException("each operation is a JSON object");
        }

        // Optional operation members may be omitted, but explicit null is never an omission.
        if (json.EnumerateObject().Any(member => member.Value.ValueKind == JsonValueKind.Null))
        {
            throw new ApiException("operation members cannot be null");
        }

        var op = RequestJson.Deserialize<TransactionOpRequest>(JsonMarshal.GetRawUtf8Value(json), "invalid_request");
        var collection = DocumentRoute.CollectionName(op.Collection);
        var hasDocument = op.Document.ValueKind != JsonValueKind.Undefined;
        var hasMerge = op.Merge.ValueKind != JsonValueKind.Undefined;
        var hasFilter = op.Filter.ValueKind != JsonValueKind.Undefined;
        var hasPatch = hasMerge || op.Ops is not null;
        var hasAssert = hasFilter || op.Absent is not null || op.Gte is not null || op.Lte is not null;
        var hasPrecondition = op.IfMatch is not null || op.IfNoneMatch is not null;
        return op.Op switch
        {
            "create" when hasDocument && !hasPatch && !hasAssert && !hasPrecondition =>
                new PutOperation(collection, op.Id is null ? Guid.CreateVersion7().ToString("D") : DocumentRoute.DocumentId(op.Id), Document(op.Document), Documents.Precondition.Absent),
            "put" when hasDocument && !hasPatch && !hasAssert =>
                new PutOperation(collection, DocumentRoute.DocumentId(op.Id), Document(op.Document), Precondition(op)),
            "patch" when hasMerge != (op.Ops is not null) && !hasDocument && !hasAssert && op.IfNoneMatch is null =>
                new PatchOperation(collection, DocumentRoute.DocumentId(op.Id), hasMerge ? DocumentPatch.Merge(op.Merge) : DocumentPatch.Ops(op.Ops), Precondition(op)),
            "delete" when !hasDocument && !hasPatch && !hasAssert && op.IfNoneMatch is null =>
                new DeleteOperation(collection, DocumentRoute.DocumentId(op.Id), Precondition(op)),
            "assert" when hasFilter != (op.Absent == true) && op.Absent != false && op.Gte is null && op.Lte is null && !hasDocument && !hasPatch && !hasPrecondition =>
                new AssertOperation(collection, DocumentRoute.DocumentId(op.Id), hasFilter ? QueryParser.Filter(op.Filter, "invalid_request") : null),
            "assert_count" when op.Id is null && (op.Gte is not null || op.Lte is not null) && (op.Gte is null or >= 0) && (op.Lte is null or >= 0)
                && !(op.Gte > op.Lte) && op.Absent is null && !hasDocument && !hasPatch && !hasPrecondition =>
                new AssertCountOperation(collection, hasFilter ? QueryParser.Filter(op.Filter, "invalid_request") : null, op.Gte, op.Lte),
            _ => throw new ApiException("the operation's kind or members are invalid"),
        };
    }

    // if_match is read like If-Match (compared strongly); if_none_match is only "*".
    private static Precondition Precondition(TransactionOpRequest op) => op.IfNoneMatch switch
    {
        null => op.IfMatch is null ? new Precondition(null, IfMatchAny: false, IfNoneMatch: false) : ETags.IfMatch(op.IfMatch),
        "*" when op.IfMatch is null => Documents.Precondition.Absent,
        _ => throw new ApiException("if_none_match is \"*\" and excludes if_match"),
    };

    private static JsonDocument Envelope(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ApiException("the body must be valid JSON");
        }
    }

    // {"ops":[…]} exactly: one ops member holding 1 to 1024 operations.
    private static List<JsonElement> Ops(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException("the body must be a JSON object");
        }

        var members = envelope.EnumerateObject().ToList();
        if (members is not [{ Name: "ops", Value.ValueKind: JsonValueKind.Array } ops])
        {
            throw new ApiException("the body is {\"ops\":[…]} with no other or repeated member");
        }

        return ops.Value.GetArrayLength() is > 0 and <= MaxOps
            ? [.. ops.Value.EnumerateArray()]
            : throw new ApiException("ops must hold 1 to 1024 operations");
    }

    private static byte[] Document(JsonElement document)
    {
        var json = JsonMarshal.GetRawUtf8Value(document).ToArray();
        RequestJson.ValidateDocument(json);
        return json;
    }
}
