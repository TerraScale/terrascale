using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>The declared schema of a collection, distinct from its inferred fields.</summary>
/// <param name="Schema">JSON Schema, or null when validation is disabled.</param>
/// <param name="Dialect">The supported JSON Schema dialect when a schema is attached.</param>
public sealed record CollectionSchemaBody(
    JsonElement? Schema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dialect = null);

/// <summary>A collection's schema or why its request failed.</summary>
public readonly union CollectionSchemaResult(CollectionSchemaBody, NotFound, Validation, PayloadTooLarge, Unavailable);

internal sealed partial class DocumentService
{
    private readonly Counter<long> _schemaAdmissions = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.schema.admission_keyword_evaluations", description: "Draft 2020-12 keyword evaluations started by schema attachment preflight.");

    public ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<CollectionSchemaResult>(database, _ => ReadCollectionSchemaAsync(database, name, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private async ValueTask<CollectionSchemaResult> ReadCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is null)
            {
                return UnknownCollection;
            }

            return SchemaBody(await transaction.GetCollectionSchemaAsync(name, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(
        TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken)
    {
        if (schema is { } declared && CollectionSchemaValidation.Check(declared, memory,
            cancellationToken: cancellationToken, evaluations: _schemaAdmissions) is { } error)
        {
            return error;
        }

        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is null)
            {
                return UnknownCollection;
            }

            try
            {
                await transaction.SetCollectionSchemaAsync(name, schema, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChangeGroupCapacityException)
            {
                return ChangeGroupCapacity;
            }
            catch (DocumentValidationException)
            {
                return new Validation("the schema must fit the replicated collection metadata limits", "invalid_schema");
            }
            catch (StorageWriteLimitException)
            {
                return WriteLimits;
            }
            catch (OverflowException)
            {
                return OffsetExhausted;
            }
        }

        return SchemaBody(schema);
    }

    private static CollectionSchemaBody SchemaBody(JsonElement? schema) =>
        new(schema?.Clone(), schema is null ? null : CollectionSchemaValidation.Dialect);
}
