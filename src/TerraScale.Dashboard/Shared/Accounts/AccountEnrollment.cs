using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Shared.Accounts;

// Immutable, minimal references survive erasure acknowledgement. Local CAS and
// fence checks cannot exclude a late write from another replica; replay must
// keep visiting these references without a time-based retirement rule.
public static class AccountEnrollment
{
    public const string Collection = "dashboard_account_enrollments";
    public sealed record Reference(string UserId, string Collection, string RecordId);

    public static async Task RememberAsync(IDashboardState state, string userId, string collection, string recordId, CancellationToken ct)
    {
        var reference = new Reference(userId, collection, recordId);
        Validate(reference);
        await ThrowIfErasedAsync(state, userId, ct);
        await state.PutAsync(Collection, AccountService.NewId(), JsonSerializer.SerializeToElement(reference), TerraBasePrecondition.Absent, ct);
        await ThrowIfErasedAsync(state, userId, ct);
    }

    public static async Task ThrowIfErasedAsync(IDashboardState state, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var key = AccountService.AccountStorageKey(userId);
        var document = await state.GetAsync(AccountService.AccountsCollection, key, ct);
        if (document is null) return;
        if (document.Id != key || document.State != "value") throw new IOException("Account identity is invalid.");
        if (AccountService.IsDeletionFence(document)) throw new AccountAccessException("Account was erased.", 401);
    }

    public static async Task<IReadOnlyList<Reference>> ListAsync(IDashboardState state, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var references = new List<Reference>();
        foreach (var document in await state.ListAsync(Collection, ct))
        {
            var reference = AccountService.Read<Reference>(document);
            if (reference.UserId != userId) continue;
            if (!TerraScaleIds.IsValid(document.Id)) throw new IOException("Enrollment reference identity is invalid.");
            Validate(reference);
            references.Add(reference);
        }
        return references;
    }

    // Encrypted sessions and credentials are handled by DashboardSessions,
    // which validates the sealed subject without asking ts-auth for a live grant.
    public static async Task CleanAsync(IDashboardState state, string userId, CancellationToken ct)
    {
        foreach (var reference in await ListAsync(state, userId, ct))
        {
            if (reference.Collection is "sessions" or "auth_credentials") continue;
            var document = await state.GetAsync(reference.Collection, reference.RecordId, ct);
            if (document is null) continue;
            if (document.Id != reference.RecordId) throw new IOException("Enrollment identity is invalid.");
            var owner = AccountService.Read<Ownership>(document);
            if (owner.UserId != userId) throw new IOException("Enrollment ownership is invalid.");
            await state.DeleteAsync(reference.Collection, reference.RecordId, document.Version, ct);
        }
    }

    private static void Validate(Reference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.UserId);
        if (reference.Collection is not ("sessions" or "auth_credentials" or "session_indexes" or AccessTokens.Collection or AccessTokens.IndexCollection) ||
            reference.RecordId is not { Length: 64 } || !reference.RecordId.All(char.IsAsciiHexDigitLower))
            throw new IOException("Enrollment reference is invalid.");
        if (reference.Collection is "session_indexes" or AccessTokens.Collection && reference.RecordId != AccountService.AccountStorageKey(reference.UserId))
            throw new IOException("Enrollment owner key is invalid.");
    }

    private sealed record Ownership(string UserId);
}
