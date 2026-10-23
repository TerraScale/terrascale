using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeOwnerAuthorization
{
    public async ValueTask<bool> AuthorizeShapeBodyAsync(string collection, DocumentOwnerProof owner, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (await GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is not { } current
            || current.Id != owner.Generation
            || await GetCollectionRulesAsync(collection, cancellationToken).ConfigureAwait(false) is not { } rules
            || rules.Owner != owner.Path || rules.Fingerprint() != owner.Policy)
        {
            return false;
        }

        return await RunAsync(() => AuthorizeShapeBody(collection, owner, body.Span, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private bool AuthorizeShapeBody(string collection, DocumentOwnerProof owner, ReadOnlySpan<byte> body, CancellationToken cancellationToken)
    {
        if (body.Length > ShapeSnapshots.SingletonBytes)
        {
            throw new InvalidDataException("a retained shape body exceeds its byte limit");
        }

        var segments = owner.Path.Split('/').Skip(1).Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal));
        var path = "$" + string.Concat(segments.Select(segment => ".\""
            + JsonEncodedText.Encode(segment, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value + "\""));
        var command = TableCommand("""
            SELECT EXISTS(SELECT 1 FROM {0} d WHERE d.id = $id
                AND json_type(d.doc, $path) IS 'text' AND json_extract(d.doc, $path) = $subject);
            """, collection);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$subject", owner.Subject);
        var idParameter = command.Parameters.AddWithValue("$id", "");
        bool Owned(string id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idParameter.Value = id;
            CountRead("shape_owner");
            return (long)command.ExecuteScalar()! == 1;
        }

        // Stream the already admitted body: no document trees, ID sets or unbounded current-document reads.
        var reader = new Utf8JsonReader(body);
        string? operation = null;
        string? operationId = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                && (reader.ValueTextEquals("offset"u8) || reader.ValueTextEquals("next_offset"u8)))
            {
                reader.Read();
                if (reader.ValueSpan.Length > 8192)
                {
                    throw new InvalidDataException("a retained shape offset exceeds its byte limit");
                }
                var offset = reader.GetString()!;
                if (offset.StartsWith("snap.", StringComparison.Ordinal))
                {
                    var encoded = offset.AsSpan(offset.LastIndexOf('.') + 1);
                    if (!encoded.SequenceEqual("0"))
                    {
                        if (encoded.Length > 1366 || !Owned(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded))))
                        {
                            return false;
                        }
                    }
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 4)
            {
                operation = null;
                operationId = null;
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 5)
            {
                if (reader.ValueTextEquals("op"u8))
                {
                    reader.Read();
                    operation = reader.GetString();
                }
                else if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    if (reader.ValueSpan.Length > 6144)
                    {
                        throw new InvalidDataException("a retained shape id exceeds its byte limit");
                    }
                    operationId = reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 4
                && operation == "upsert" && (operationId is null || !Owned(operationId)))
            {
                return false;
            }
        }
        // Deletes retain their generation/path/subject/policy proof from sealing; current ownership is needed for payloads.
        return true;
    }
}
