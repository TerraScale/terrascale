using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using TerraBase.Primitives;

namespace TerraBase.Features.Documents;

/// <summary>The collection's rules; null denies every end-user operation.</summary>
/// <param name="Rules">Current policy, or null before one is declared.</param>
public sealed record CollectionRulesBody(CollectionAccessRules? Rules);

/// <summary>Rules or an opaque metadata failure.</summary>
public readonly union CollectionRulesResult(CollectionRulesBody, NotFound, Validation, PayloadTooLarge, Unavailable);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(CollectionAccessRules))]
[JsonSerializable(typeof(CollectionRulesBody))]
[JsonSerializable(typeof(DocumentOwnerProof))]
internal sealed partial class CollectionRulesJsonContext : JsonSerializerContext;

internal sealed partial class DocumentService
{
    public ValueTask<CollectionRulesResult> GetCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<CollectionRulesResult>(database, _ => ReadCollectionRulesAsync(database, name, cancellationToken),
            static () => DocumentRoute.UnknownDatabase, cancellationToken);

    private async ValueTask<CollectionRulesResult> ReadCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is null
                ? UnknownCollection : new CollectionRulesBody(await transaction.GetCollectionRulesAsync(name, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask<CollectionRulesResult> SetCollectionRulesAsync(
        TenantContext database, string name, CollectionAccessRules rules, CancellationToken cancellationToken)
    {
        if (rules.Check() is { } invalid)
        {
            return invalid;
        }

        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                if (!await transaction.SetCollectionRulesAsync(name, rules, cancellationToken).ConfigureAwait(false))
                {
                    return UnknownCollection;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CollectionRulesBody(rules);
            }
            catch (ChangeGroupCapacityException)
            {
                return ChangeGroupCapacity;
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
    }

    private static async ValueTask<DocumentRejected?> AuthorizeOperationAsync(
        IStorageTransaction transaction, DocumentOperation operation, ProjectAccess? access, CancellationToken cancellationToken)
    {
        if (access is not { IsEndUser: true })
        {
            return null;
        }

        var rules = await transaction.GetCollectionRulesAsync(operation.Collection, cancellationToken).ConfigureAwait(false);
        var write = operation is PutOperation or PatchOperation or DeleteOperation;
        if (!CollectionRuleAccess.Granted(rules, access, write))
        {
            return AccessDenied;
        }

        if (operation is AssertCountOperation)
        {
            return null;
        }

        var id = operation switch
        {
            PutOperation put => put.Id,
            PatchOperation patch => patch.Id,
            DeleteOperation delete => delete.Id,
            AssertOperation assert => assert.Id,
            _ => throw new InvalidOperationException("unknown operation"),
        };
        var current = await transaction.GetAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false);
        if (!write)
        {
            // Even an absence assertion cannot be used to probe someone else's document.
            return current is null || CollectionRuleAccess.CanRead(rules, current.Json, access) ? null : AccessDenied;
        }

        var policy = rules!.Write!;
        if (current is not null && !CollectionRuleAccess.CollectionWide(policy, access) && rules.Subject(current.Json) != access.Actor)
        {
            return AccessDenied;
        }

        if (operation is PutOperation putOperation && rules.Owner is not null)
        {
            _ = DocumentJson.Read(putOperation.Json, requireObject: true);
            var owner = rules.Subject(putOperation.Json);
            // An end-user cannot claim a caller-selected existing id, transfer ownership, or recreate an owned tombstone.
            if (current is not null ? owner != rules.Subject(current.Json) : owner != access.Actor)
            {
                return AccessDenied;
            }

            if (current is null && await transaction.GetVersionAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false) is not null
                && await transaction.GetCollectionAsync(operation.Collection, cancellationToken).ConfigureAwait(false) is { } collection
                && !CollectionRuleAccess.ProofMatches(await transaction.GetDocumentOwnerProofAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false), collection, rules, access))
            {
                return AccessDenied;
            }
        }

        return null;
    }

    private static readonly DocumentRejected AccessDenied = new(new Forbidden("the collection rules deny this operation", "access_denied"));
}
