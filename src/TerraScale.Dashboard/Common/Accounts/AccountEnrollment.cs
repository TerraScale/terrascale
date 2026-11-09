using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts
{
    // Immutable, minimal references survive erasure acknowledgement. Local CAS and
    // fence checks cannot exclude a late write from another replica; replay must
    // keep visiting these references without a time-based retirement rule.
    public static class AccountEnrollment
    {
        public const string Table = "dashboard_account_enrollments";
        public sealed record Reference(string UserId, string Table, string RecordId);

        public static async Task RememberAsync(IDashboardState state, string userId, string table, string recordId, CancellationToken ct)
        {
            var reference = new Reference(userId, table, recordId);
            Validate(reference);
            await ThrowIfErasedAsync(state, userId, ct);
            await state.PutAsync(Table, AccountService.NewId(), JsonSerializer.SerializeToElement(reference), StatePrecondition.Absent, ct);
            await ThrowIfErasedAsync(state, userId, ct);
        }

        public static async Task ThrowIfErasedAsync(IDashboardState state, string userId, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            var key = AccountService.AccountStorageKey(userId);
            var row = await state.GetAsync(AccountService.AccountsTable, key, ct);
            if (row is null)
            {
                return;
            }

            if (row.Id != key)
            {
                throw new IOException("Account identity is invalid.");
            }

            if (AccountService.IsDeletionFence(row))
            {
                throw new AccountAccessException("Account was erased.", 401);
            }
        }

        public static async Task<IReadOnlyList<Reference>> ListAsync(IDashboardState state, string userId, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            var references = new List<Reference>();
            foreach (var row in await state.ListAsync(Table, ct))
            {
                var reference = AccountService.Read<Reference>(row);
                if (reference.UserId != userId)
                {
                    continue;
                }

                if (!TerraScaleIds.IsValid(row.Id))
                {
                    throw new IOException("Enrollment reference identity is invalid.");
                }

                Validate(reference);
                references.Add(reference);
            }
            return references;
        }

        public static async Task CleanAsync(IDashboardState state, string userId, CancellationToken ct)
        {
            foreach (var reference in await ListAsync(state, userId, ct))
            {
                var row = await state.GetAsync(reference.Table, reference.RecordId, ct);
                if (row is null)
                {
                    continue;
                }

                if (row.Id != reference.RecordId)
                {
                    throw new IOException("Enrollment identity is invalid.");
                }

                var owner = AccountService.Read<Ownership>(row);
                if (owner.UserId != userId)
                {
                    throw new IOException("Enrollment ownership is invalid.");
                }

                await state.DeleteAsync(reference.Table, reference.RecordId, row.Version, ct);
            }
        }

        private static void Validate(Reference reference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference.UserId);
            if (reference.Table is not (AccessTokens.Table or AccessTokens.IndexTable) ||
                reference.RecordId is not { Length: 64 } || !reference.RecordId.All(char.IsAsciiHexDigitLower))
            {
                throw new IOException("Enrollment reference is invalid.");
            }

            if (reference.Table is AccessTokens.Table && reference.RecordId != AccountService.AccountStorageKey(reference.UserId))
            {
                throw new IOException("Enrollment owner key is invalid.");
            }
        }

        private sealed record Ownership(string UserId);
    }
}
