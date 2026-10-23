using System.Buffers;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>GET /v1/projects/{project}/databases/{database}/export</c>: streams every live document as NDJSON.</summary>
public sealed class ExportDatabaseEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <summary>The export's media type: one JSON object per line.</summary>
    public const string NdjsonMediaType = "application/x-ndjson";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.DatabaseRoute + "/export");
        Name("exportDatabase");
        Summary("Exports every live document as NDJSON");
        Description("Streams one {\"collection\",\"id\",\"document\"} object per line, in collection then id order, from one read snapshot. "
            + "To resume an interrupted export, pass after_collection and after_id of the last line received; the resumed part reads a new snapshot. "
            + "A stream that ends without its final chunk is incomplete.");
        Tags("Documents");
        Produces<ExportLineBody>(StatusCodes.Status200OK, NdjsonMediaType);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DocumentRoute.CheckParameters(HttpContext, "after_collection", "after_id") switch
        {
            Validation error => ErrorResponse.From(error),
            IQueryCollection query => After(query) switch
            {
                Validation error => ErrorResponse.From(error),
                ExportStart start => await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
                {
                    Validation error => ErrorResponse.From(error),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    TenantContext database => TypedResults.Stream(
                        body => WriteAsync(body, documents.ExportAsync(database, start.Collection, start.Id, cancellationToken), cancellationToken), NdjsonMediaType),
                },
            },
        };

    private static Valid<ExportStart> After(IQueryCollection query)
    {
        var hasCollection = query.TryGetValue("after_collection", out var collection);
        if (hasCollection != query.ContainsKey("after_id"))
        {
            return new Validation("after_collection and after_id go together");
        }

        if (!hasCollection)
        {
            return new ExportStart(null, null);
        }

        return (DocumentRoute.CheckCollectionName(collection.ToString()), DocumentRoute.CheckStoredId(query["after_id"].ToString())) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (string name, string id) => new ExportStart(name, id),
        };
    }

    private static async Task WriteAsync(Stream body, IAsyncEnumerable<ExportRow> rows, CancellationToken cancellationToken)
    {
        // Lines collect in one buffer and reach the response in chunks of about 64 KiB, not one write per document.
        const int Chunk = 64 * 1024;
        var buffer = new ArrayBufferWriter<byte>(Chunk);
        using var writer = new Utf8JsonWriter(buffer);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("collection"u8, row.Collection);
            writer.WriteString("id"u8, row.Document.Id);
            writer.WritePropertyName("document"u8);
            // Stored JSON was validated on write; copying it avoids a parse per document.
            writer.WriteRawValue(row.Document.Json.Span, skipInputValidation: true);
            writer.WriteEndObject();
            writer.Flush();
            writer.Reset();
            buffer.Write("\n"u8);
            if (buffer.WrittenCount >= Chunk)
            {
                await body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
                buffer.ResetWrittenCount();
            }
        }

        await body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Where an export starts: after this document, or at the beginning when both are null.</summary>
internal sealed record ExportStart(string? Collection, string? Id);
