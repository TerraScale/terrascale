using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Shapes;

/// <summary>Trusted authorization kept separate from the client's optional where predicate.</summary>
internal sealed record ShapeAccess(CollectionAccessRules Rules, ProjectAccess Identity, DocumentOwnerProof? Owner)
{
    public static readonly Forbidden Denied = new("The request cannot be served.");

    public string Canonical(string userFilter)
    {
        // Length-prefixed fields avoid ambiguity even when a signed subject contains delimiters.
        var fields = new[] { Rules.Fingerprint(), Identity.Actor,
            Identity.Roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(Identity.Roles.Order(StringComparer.Ordinal))
            .Concat([Owner?.Path ?? "", Owner?.Subject ?? "", userFilter]);
        var identity = string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
        return "access:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static async ValueTask<ShapeAccessResult> BindAsync(IStorageEngine engine, ShapeScope scope, ProjectAccess? identity,
        CancellationToken cancellationToken)
    {
        if (identity is not { IsEndUser: true })
        {
            return scope;
        }

        try
        {
            if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return new NotFound("the database does not exist", "unknown_database");
            }

            await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            if (await transaction.GetCollectionAsync(scope.Collection.Name, cancellationToken).ConfigureAwait(false) is not { } collection)
            {
                return new NotFound("the collection does not exist", "unknown_collection");
            }

            var rules = await transaction.GetCollectionRulesAsync(collection.Name, cancellationToken).ConfigureAwait(false);
            if (rules is null || rules.Check() is not null || !CollectionRuleAccess.Granted(rules, identity, write: false))
            {
                return Denied;
            }

            var owner = CollectionRuleAccess.CollectionWide(rules.Read, identity) ? null
                : new DocumentOwnerProof(collection.Id, rules.Owner!, identity.Actor, rules.Fingerprint());
            var filter = scope.Filter.WithAccess(new ShapeAccess(rules, identity, owner));
            return scope with { Collection = collection, CanonicalFilter = filter.Canonical, Filter = filter };
        }
        catch (Exception error) when (error is StorageBusyException or ObjectDisposedException or InvalidDataException or JsonException or QosMemoryException)
        {
            return new Unavailable("shape authorization is unavailable; retry later");
        }
    }
}

internal readonly union ShapeAccessResult(ShapeScope, Forbidden, NotFound, Unavailable);
