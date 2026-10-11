using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Schema;

internal sealed class UniqueSchemaService(IStorageEngine engine, IReplicationWait replication) : IUniqueSchemaService
{
    public async ValueTask<DeclareUniqueSchemaResult> DeclareAsync(
        TenantContext database, string collection, UniqueSchemaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Constraints(request) is not { } constraints)
        {
            return new Validation("unique schemas hold at most 32 named scalar keys, each with 1 to 4 distinct dotted fields and a supported normalization");
        }

        if (await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return new NotFound();
        }

        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            try
            {
                await transaction.SetUniqueConstraintsAsync(collection, constraints, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UniqueConstraintException)
            {
                return new Conflict("changing unique keys requires an empty collection", "schema_requires_empty_collection");
            }
        }

        await replication.WaitAsync(database, cancellationToken).ConfigureAwait(false);
        return new UniqueSchema(constraints);
    }

    private static IReadOnlyList<UniqueConstraint>? Constraints(UniqueSchemaRequest request)
    {
        if (request.Fields?.Count > 128 || request.Indexes?.Count > UniqueKeys.MaxConstraints)
        {
            return null;
        }

        var constraints = new List<UniqueConstraint>();
        foreach (var field in request.Fields ?? [])
        {
            if (field is null || !DocumentField.TryParse(field.Name, out var parsed) || parsed.IsMetadata)
            {
                return null;
            }

            if (field.Unique)
            {
                var name = "field_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(field.Name!)))[..16];
                constraints.Add(new UniqueConstraint(name, [field.Name!], field.Normalization ?? "none"));
            }
        }

        foreach (var index in request.Indexes ?? [])
        {
            if (index is null || index.Name is null || index.Fields is null)
            {
                return null;
            }

            if (index.Unique)
            {
                constraints.Add(new UniqueConstraint(index.Name, index.Fields, index.Normalization ?? "none"));
            }
        }

        return UniqueKeys.Valid(constraints) ? [.. constraints.OrderBy(constraint => constraint.Name, StringComparer.Ordinal)] : null;
    }
}
