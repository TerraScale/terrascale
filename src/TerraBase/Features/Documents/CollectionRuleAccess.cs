using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using TerraBase.Engine;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>Evaluates signed identities; filters enter the engine before counts, sort and pagination.</summary>
internal static class CollectionRuleAccess
{
    public static bool EndUser => ProjectAccess.Current?.IsEndUser == true;

    public static bool CollectionWide(CollectionAccessPolicy? policy, ProjectAccess access) =>
        policy is not null && (policy.Public || policy.Roles is { } roles && roles.Any(access.Roles.Contains));

    public static bool CanRead(CollectionAccessRules? rules, ReadOnlyMemory<byte> json, ProjectAccess access) =>
        CollectionWide(rules?.Read, access) || rules?.Read?.Owner == true && access.Actor.Length > 0 && rules.Subject(json) == access.Actor;

    public static bool Granted(CollectionAccessRules? rules, ProjectAccess access, bool write) =>
        (write ? rules?.Write : rules?.Read) is { } policy && (CollectionWide(policy, access) || policy.Owner && access.Actor.Length > 0);

    public static DocumentFilter? Filter(CollectionAccessRules rules, ProjectAccess access) =>
        CollectionWide(rules.Read, access) ? null : rules.OwnerFilter(access.Actor);

    public static DocumentFilter? And(DocumentFilter? left, DocumentFilter? right) =>
        left is null ? right : right is null ? left : new AndFilter([left, right]);

    public static bool ProofMatches(DocumentOwnerProof? proof, CollectionInfo collection, CollectionAccessRules rules, ProjectAccess access) =>
        proof is not null && proof.Generation == collection.Id && proof.Path == rules.Owner
        && proof.Policy == rules.Fingerprint() && proof.Subject == access.Actor;

    public static async ValueTask<string> BindCursorAsync(
        IDocumentService documents, TenantContext database, string collection, string digest, CancellationToken cancellationToken)
    {
        if (ProjectAccess.Current is not { IsEndUser: true } access)
        {
            return digest;
        }

        var result = await documents.GetCollectionRulesAsync(database, collection, cancellationToken).ConfigureAwait(false);
        var policy = result is CollectionRulesBody body ? body.Rules?.Fingerprint() ?? "deny" : "deny";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[4];
        foreach (var value in access.Roles.Order(StringComparer.Ordinal).Prepend(access.Roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Prepend(access.Actor).Append(policy).Append(digest))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
