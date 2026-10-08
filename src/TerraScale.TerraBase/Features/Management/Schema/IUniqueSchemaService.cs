using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Schema;

/// <summary>Unique declarations using the SDK's fields[].unique and indexes[].unique shape.</summary>
/// <param name="Fields">Single-field declarations.</param>
/// <param name="Indexes">Compound declarations.</param>
public sealed record UniqueSchemaRequest(IReadOnlyList<UniqueSchemaField>? Fields, IReadOnlyList<UniqueSchemaIndex>? Indexes);

/// <summary>One declared field; only fields marked unique add a constraint.</summary>
/// <param name="Name">Dotted member path.</param>
/// <param name="Unique">Whether to enforce uniqueness.</param>
/// <param name="Normalization">none, case_fold, or trim_case_fold.</param>
public sealed record UniqueSchemaField(string? Name, bool Unique = false, string? Normalization = null);

/// <summary>One declared compound key.</summary>
/// <param name="Name">Lowercase constraint name.</param>
/// <param name="Fields">One to four dotted member paths.</param>
/// <param name="Unique">Whether to enforce uniqueness.</param>
/// <param name="Normalization">none, case_fold, or trim_case_fold.</param>
public sealed record UniqueSchemaIndex(string? Name, IReadOnlyList<string>? Fields, bool Unique = false, string? Normalization = null);

/// <summary>A saved declaration or its input/state error.</summary>
public readonly union DeclareUniqueSchemaResult(UniqueSchema, NotFound, Conflict, Validation);

/// <summary>Declares collection uniqueness before consumers write records.</summary>
public interface IUniqueSchemaService
{
    /// <summary>Replaces an empty collection's declaration; an unchanged declaration is idempotent.</summary>
    ValueTask<DeclareUniqueSchemaResult> DeclareAsync(TenantContext database, string collection, UniqueSchemaRequest request, CancellationToken cancellationToken);
}
