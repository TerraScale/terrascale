using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>One redacted failure; hashes identify UTF-8 IDs and JSON Pointers without returning their text.</summary>
public sealed record CollectionSchemaPreviewDiagnostic(
    [property: JsonPropertyName("id_hash")] string IdHash,
    [property: JsonPropertyName("path_hash")] string PathHash, string Keyword);

/// <summary>Counts for this page only, observed at a copy-local frontier; no schema or documents were changed.</summary>
public sealed record CollectionSchemaPreviewBody(
    [property: JsonPropertyName("schema_hash")] string SchemaHash, string Frontier,
    int Examined, int Valid, int Invalid, bool Complete,
    [property: JsonPropertyName("next_cursor")] string? NextCursor, IReadOnlyList<CollectionSchemaPreviewDiagnostic> Diagnostics);

/// <summary>A preview page or an admission, authorization, or continuation failure.</summary>
public readonly union CollectionSchemaPreviewResult(CollectionSchemaPreviewBody, NotFound, Validation, Conflict, Forbidden, Unavailable);

internal sealed partial class DocumentService
{
    private readonly IDataProtector _schemaPreviewProtector = new EphemeralDataProtectionProvider().CreateProtector("TerraBase.schema-preview.v1");

    public ValueTask<CollectionSchemaPreviewResult> PreviewCollectionSchemaAsync(
        TenantContext database, string name, JsonElement schema, string? cursor, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<CollectionSchemaPreviewResult>(database, async _ =>
        {
            if (CollectionRuleAccess.EndUser)
            {
                return new Forbidden("project credentials are required");
            }

            if (CollectionSchemaValidation.Check(schema, memory, evaluations: _schemaAdmissions, cancellationToken: cancellationToken) is { } invalid)
            {
                return invalid;
            }

            if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
            {
                return DocumentRoute.UnknownDatabase;
            }

            await using (transaction.ConfigureAwait(false))
            {
                if (await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is not { } collection)
                {
                    return UnknownCollection;
                }

                var schemaHash = Convert.ToHexStringLower(SHA256.HashData(JsonMarshal.GetRawUtf8Value(schema)));
                var incarnation = await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
                var offset = await transaction.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
                var frontier = PreviewHash($"{transaction.Scope}\0{incarnation}\0{collection.Id}\0{offset.ToString(CultureInfo.InvariantCulture)}");
                var binding = PreviewHash($"{schemaHash}\0{frontier}");
                string? after = null;
                if (cursor is not null)
                {
                    try
                    {
                        if (cursor.Length > 2048)
                        {
                            return new Validation("preview cursors are at most 2048 characters", "invalid_cursor");
                        }

                        var position = _schemaPreviewProtector.Unprotect(cursor);
                        if (!position.StartsWith(binding + '\0', StringComparison.Ordinal))
                        {
                            return new Conflict("the schema or local data frontier changed; restart the preview", "preview_stale");
                        }

                        after = position[(binding.Length + 1)..];
                    }
                    catch (CryptographicException)
                    {
                        return new Validation("the preview cursor is invalid or belongs to another process; restart", "invalid_cursor");
                    }
                }

                var page = await transaction.ListAsync(name, after, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                var failures = 0;
                var diagnostics = new List<CollectionSchemaPreviewDiagnostic>();
                foreach (var document in page.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        CollectionSchemaValidation.Validate(schema, document.Json, cancellationToken, memory);
                    }
                    catch (CollectionSchemaException error)
                    {
                        failures++;
                        diagnostics.AddRange(error.Details.Take(16 - diagnostics.Count)
                            .Select(detail => new CollectionSchemaPreviewDiagnostic(PreviewHash(document.Id), PreviewHash(detail.Path), detail.Keyword)));
                    }
                }

                usage.RecordRead(database, page.Documents.Sum(document => OperationUnits.Of(document.Json.Length)));
                var next = page.HasMore ? _schemaPreviewProtector.Protect(binding + '\0' + page.Documents[^1].Id) : null;
                return new CollectionSchemaPreviewBody(schemaHash, frontier, page.Documents.Count,
                    page.Documents.Count - failures, failures, !page.HasMore, next, diagnostics);
            }
        }, static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private static string PreviewHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
