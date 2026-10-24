using System.Text.Json;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Closes a platform user's auth account durably (docs/terrabase/project-closure.md): an account intent and a fence on every
/// organization where it is the only member, committed first; then sole ownership read again under the fences; then each
/// of those organizations' projects closed through TerraBase; then those organizations, the shared memberships and the user
/// with everything keyed by it. A crash or restart resumes from the intent (<see cref="ClosureWorker"/>).
/// </summary>
public sealed class AccountDeletion(
    PlatformDirectory platform,
    Closures closures,
    Users users,
    AuthStore store,
    TimeProvider clock,
    IOpenIddictAuthorizationStore<OidcAuthorization> authorizations,
    IOpenIddictTokenStore<OidcToken> tokens,
    ILogger<AccountDeletion> logger)
{
    /// <summary>
    /// Deletes <paramref name="user"/>'s account: true once deleted, false while project closures are pending (it finishes
    /// in the background). A 409 (<c>last_owner</c>, <c>not_empty</c>, <c>closure_conflict</c>, <c>binding_conflict</c>) before
    /// any removal ends the account intent: the account, its memberships and organizations stay, but project closures it
    /// already started (which may hold a permanent TerraBase fence) are not rolled back; they finish on their own. Once
    /// removals began, a 409 only pauses the closure until the cause is resolved; repeating the request resumes it.
    /// </summary>
    public async Task<bool> DeleteAsync(UserRecord user, CancellationToken cancellationToken, string? receiptHash = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var intent = await platform.CommitAsync(async () =>
        {
            var current = await platform.ClosureAsync("account", user.Id, cancellationToken).ConfigureAwait(false);
            var created = ClosureJob.New(PlatformDirectory.Instance, "account", user.Id, null, user.Id, clock);
            var closure = current?.Value ?? created.Job;
            List<StoreOperation> operations =
            [
                AuthStore.Require(AuthStore.UserCollection, user.InstanceId, user.Id,
                    PlatformDirectory.Filter("eq", new System.Text.Json.Nodes.JsonArray("/authStamp", user.AuthStamp))),
            ];
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            if (current is null)
            {
                var (sole, _) = await platform.AccountScopeAsync(user.Id, cancellationToken).ConfigureAwait(false);
                candidates.UnionWith(sole);
                operations.Add(created.Create);
                foreach (var organizationId in sole)
                {
                    operations.Add(await platform.OwnerAssertionAsync(user.Id, organizationId, cancellationToken).ConfigureAwait(false) ?? throw ApiException.Unavailable());
                    operations.Add(ClosureJob.New(PlatformDirectory.Instance, "organization", organizationId, organizationId, user.Id, clock, user.Id).Create);
                }
            }
            else
            {
                // Pin the exact creation and version while attaching, before a worker can remove it.
                operations.Add(AuthStore.Replace(AuthStore.ClosureJobCollection, user.InstanceId, ClosureJob.Key("account", user.Id),
                    JsonSerializer.SerializeToElement(closure, ClosureJson.Default.ClosureJob), current.Version));
            }

            if (receiptHash is not null)
            {
                var existing = await store.GetAsync(AuthStore.AccountDeletionReceiptCollection, user.InstanceId, receiptHash, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var receipt = existing.Document.Deserialize(DeletionJson.Default.AccountDeletionReceipt);
                    if (receipt is null || receipt.SubjectId != user.Id || receipt.Generation != closure.Generation || receipt.ExpiresAt <= clock.GetUtcNow())
                    {
                        throw PlatformDirectory.Conflict("deletion_refused", "The receipt does not identify this account closure.");
                    }
                }
                else
                {
                    if (current is not null)
                    {
                        await foreach (var owned in this.IntentsAsync(user.Id, "organization", cancellationToken).ConfigureAwait(false))
                        {
                            candidates.Add(owned.Value.SubjectId);
                        }
                    }

                    var receipt = new AccountDeletionReceipt(user.InstanceId, receiptHash, user.Id, closure.Generation!, clock.GetUtcNow() + DeletionJob.CredentialBound, [.. candidates]);
                    operations.Add(AuthStore.Create(AuthStore.AccountDeletionReceiptCollection, user.InstanceId, receiptHash,
                        JsonSerializer.SerializeToElement(receipt, DeletionJson.Default.AccountDeletionReceipt)));
                }
            }

            return new PlatformDirectory.Plan<ClosureJob>(operations, closure);
        }, cancellationToken).ConfigureAwait(false);
        var job = await platform.ClosureAsync("account", user.Id, cancellationToken).ConfigureAwait(false);
        if (job is null || !job.Value.Same(intent))
        {
            // A worker may finish immediately after the attachment transaction. Absence alone proves nothing.
            return await this.CompletedAsync(user.Id, intent.Generation!, cancellationToken).ConfigureAwait(false)
                ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
        }

        return await this.AdvanceAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only opaque status; neither token validity nor user absence is completion evidence.</summary>
    public async Task<bool> StatusAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken)
    {
        var receipt = await this.ReadReceiptAsync(receiptHash, organizationId, cancellationToken).ConfigureAwait(false);

        var job = await platform.ClosureAsync("account", receipt.SubjectId, cancellationToken).ConfigureAwait(false);
        if (await this.CompletedAsync(receipt.SubjectId, receipt.Generation, cancellationToken).ConfigureAwait(false))
        {
            if (organizationId is null)
            {
                return true;
            }

            var key = DeletionJob.Key(PlatformDirectory.Instance, "organization", organizationId);
            var proof = (await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, key, cancellationToken).ConfigureAwait(false))?.Document.Deserialize(DeletionJson.Default.DeletionJob);
            return proof is not null && proof.Id == key && proof.InstanceId == PlatformDirectory.Instance && proof.Scope == "organization" && proof.SubjectId == organizationId;
        }

        if (job?.Value.Generation == receipt.Generation)
        {
            return false;
        }

        throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
    }

    /// <summary>Correlates an authorized logical deletion to its history request without advancing either worker.</summary>
    internal async Task<AccountDeletionHistory?> HistoryAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken)
    {
        if (!await this.StatusAsync(receiptHash, organizationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var receipt = await this.ReadReceiptAsync(receiptHash, organizationId, cancellationToken).ConfigureAwait(false);
        var scope = organizationId is null ? "account" : "organization";
        var subject = organizationId ?? receipt.SubjectId;
        var key = DeletionJob.Key(PlatformDirectory.Instance, scope, subject);
        var document = await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, key, cancellationToken).ConfigureAwait(false);
        var job = document?.Document.Deserialize(DeletionJson.Default.DeletionJob);
        return job is not null && job.Id == key && job.InstanceId == PlatformDirectory.Instance && job.Scope == scope && job.SubjectId == subject
            && (organizationId is not null || job.ClosureGeneration == receipt.Generation) && job.ErasureId is { } erasure && PlatformDirectory.IsId(erasure)
            ? new AccountDeletionHistory(erasure, job.RequestedAt, job.AuthLogicalVerifiedAt, job.HistoryStatus, job.HistoryCompletedAt)
            : null;
    }

    private async Task<AccountDeletionReceipt> ReadReceiptAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.AccountDeletionReceiptCollection, PlatformDirectory.Instance, receiptHash, cancellationToken).ConfigureAwait(false);
        var receipt = document?.Document.Deserialize(DeletionJson.Default.AccountDeletionReceipt);
        return receipt is not null && receipt.InstanceId == PlatformDirectory.Instance && receipt.TokenHash == receiptHash && receipt.ExpiresAt > clock.GetUtcNow()
            && (organizationId is null || receipt.OrganizationIds.Contains(organizationId, StringComparer.Ordinal))
            ? receipt : throw PlatformDirectory.NotFound();
    }

    private async Task<bool> CompletedAsync(string userId, string generation, CancellationToken cancellationToken)
    {
        var key = DeletionJob.Key(PlatformDirectory.Instance, "account", userId);
        var document = await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, key, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(DeletionJson.Default.DeletionJob) is { } proof
            && proof.Id == key && proof.InstanceId == PlatformDirectory.Instance && proof.Scope == "account" && proof.SubjectId == userId && proof.ClosureGeneration == generation;
    }

    /// <summary>
    /// One resumable step of an account closure: true when the account is gone, false while pending. Before it removes
    /// anything, a refusal ends the intent (see <see cref="CancelAsync"/>). Once it began removing organizations,
    /// memberships or the user (<see cref="ClosureJob.CommittedAt"/>), it never cancels: a late refusal pauses it and throws.
    /// </summary>
    internal async Task<bool> AdvanceAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var userId = job.Value.SubjectId;
        if (await users.GetAsync(PlatformDirectory.Instance, userId, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            // Deleted after its last step; only the intent was left.
            await store.CompareAndDeleteAsync(AuthStore.ClosureJobCollection, job.Value.InstanceId, ClosureJob.Key("account", userId), job.Version, cancellationToken).ConfigureAwait(false);
            return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken).ConfigureAwait(false)
                ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
        }

        if (job.Value.CommittedAt is null && await store.IsClosureCancellingAsync(job.Value, cancellationToken).ConfigureAwait(false))
        {
            // A pass that read before another region began cancelling must never begin removals. The barrier resumes
            // through the same handoff; a committed receipt means cancellation lost and the committed driver may continue.
            await this.CancelAsync(job, "refused", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Read under the fences: a member who joined before them makes an organization shared, and nobody can join now.
        IReadOnlyList<string> sole, shared;
        try
        {
            (sole, shared) = await platform.AccountScopeAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
        {
            await this.RefuseAsync(job, error, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await foreach (var stale in this.IntentsAsync(userId, "organization", cancellationToken).ConfigureAwait(false))
        {
            if (!sole.Contains(stale.Value.SubjectId))
            {
                // Shared again, or gone: its fence must not keep other members out. Its started project closures go on alone.
                await platform.CancelClosureAsync(stale, cancellationToken).ConfigureAwait(false);
            }
        }

        // An organization that became sole later (its other members left) closes too; its fence asserts the owner role.
        foreach (var organizationId in sole)
        {
            if (await platform.ClosureAsync("organization", organizationId, cancellationToken).ConfigureAwait(false) is not { Value.AccountId: { } owner } || owner != userId)
            {
                await platform.CommitAsync(async () => new PlatformDirectory.Plan<bool>(
                [
                    await platform.OwnerAssertionAsync(userId, organizationId, cancellationToken).ConfigureAwait(false) ?? throw ApiException.Unavailable(),
                    AuthStore.Require(AuthStore.ClosureJobCollection, PlatformDirectory.Instance, ClosureJob.Key("account", userId), PlatformDirectory.Filter("exists", "/id")),
                    ClosureJob.New(PlatformDirectory.Instance, "organization", organizationId, organizationId, userId, clock, userId).Create,
                ], true), cancellationToken).ConfigureAwait(false);
            }
        }

        var pending = false;
        foreach (var organizationId in sole)
        {
            await foreach (var projectId in platform.ProjectIdsAsync(organizationId, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var project = await platform.RequestProjectClosureAsync(userId, organizationId, projectId, userId, cancellationToken).ConfigureAwait(false);
                    pending |= !await closures.AdvanceAsync(project, cancellationToken).ConfigureAwait(false);
                }
                catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
                {
                    // Removed meanwhile (by its own closure, or another pass): nothing left to close.
                }
                catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
                {
                    // TerraBase refused a project (it holds data, or another binding): the account stays.
                    await this.RefuseAsync(job, error, cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }

        if (pending)
        {
            await closures.DeferAsync(job, "project_closure_pending", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // From here on removals are irreversible: record it first, so no later refusal cancels a half-removed account.
        for (var attempt = 0; job.Value.CommittedAt is null; attempt++)
        {
            var committed = job.Value with { CommittedAt = clock.GetUtcNow() };
            if (await store.CommitAccountClosureAsync(committed, job.Version, cancellationToken).ConfigureAwait(false) is { } version)
            {
                job = new(committed, version);
                break;
            }

            // Another pass may defer, commit or finish this generation while its projects close. A lost version is
            // not an outage, and an absent or replaced intent is completion only with this generation's durable proof.
            if (await platform.ClosureAsync("account", userId, cancellationToken).ConfigureAwait(false) is not { } reread || !reread.Value.Same(job.Value))
            {
                return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken).ConfigureAwait(false)
                    ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
            }

            job = reread;
            if (job.Value.CommittedAt is null && await store.IsClosureCancellingAsync(job.Value, cancellationToken).ConfigureAwait(false))
            {
                await this.CancelAsync(job, "refused", cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (attempt == 3 && job.Value.CommittedAt is null)
            {
                throw ApiException.Unavailable();
            }
        }

        // Shared memberships first: a late last_owner there pauses the closure before any organization goes.
        foreach (var organizationId in shared)
        {
            try
            {
                await platform.RemoveAsync(userId, organizationId, userId, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
            {
                // Gone meanwhile, or a stale collaborator record that grants nothing.
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
            {
                await this.RefuseAsync(job, error, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var organizationId in sole)
        {
            try
            {
                await platform.DeleteOrganizationAsync(userId, organizationId, closingAccount: true, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
            {
                // Removed by an earlier pass.
            }

            if (await platform.ClosureAsync("organization", organizationId, cancellationToken).ConfigureAwait(false) is { } intent)
            {
                await platform.CancelClosureAsync(intent, cancellationToken).ConfigureAwait(false);
            }
        }

        await users.DeleteAsync(user.InstanceId, user.Id, job.Value.Generation, cancellationToken).ConfigureAwait(false);
        await this.CleanUpAsync(user, cancellationToken).ConfigureAwait(false);
        await store.CompareAndDeleteAsync(AuthStore.ClosureJobCollection, job.Value.InstanceId, ClosureJob.Key("account", userId), job.Version, cancellationToken).ConfigureAwait(false);
        return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken).ConfigureAwait(false)
            ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
    }

    // A refusal before any removal ends the intent; after one, it only pauses it (the caller still gets the 409).
    private async Task RefuseAsync(Versioned<ClosureJob> job, ApiException error, CancellationToken cancellationToken)
    {
        var code = error.Code is { Length: <= 64 } known ? known : "refused";
        if (job.Value.CommittedAt is not null)
        {
            await closures.DeferAsync(job, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        await this.CancelAsync(job, code, cancellationToken).ConfigureAwait(false);
    }

    // Every holder fences the generation before the parent is removed anywhere. A pending barrier cannot return the
    // refusal yet (503); a holder that began irreversible removals keeps the driver and pauses it. Only after all holders
    // acknowledged removal are projects detached and organization fences removed. Their guarded cleanup cannot touch a
    // newer account generation, and the worker recovers any leftover children after a crash.
    private async Task CancelAsync(Versioned<ClosureJob> job, string code, CancellationToken cancellationToken)
    {
        var current = job;
        for (var attempt = 0; ; attempt++)
        {
            if (current.Value.CommittedAt is not null)
            {
                await closures.DeferAsync(current, code, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (await platform.CancelClosureAsync(current, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            if (attempt == 3)
            {
                throw ApiException.Unavailable();
            }

            if (await platform.ClosureAsync("account", job.Value.SubjectId, cancellationToken).ConfigureAwait(false) is not { } reread || !reread.Value.Same(job.Value))
            {
                return;
            }

            current = reread;
        }

        var userId = job.Value.SubjectId;
        await foreach (var project in this.IntentsAsync(userId, "project", cancellationToken).ConfigureAwait(false))
        {
            _ = await closures.DetachAsync(project, cancellationToken).ConfigureAwait(false);
        }

        // Each guarded by the absence of an account intent: a newer request's account closure keeps its own.
        await foreach (var intent in this.IntentsAsync(userId, "organization", cancellationToken).ConfigureAwait(false))
        {
            await platform.CancelClosureAsync(intent, cancellationToken, unlessAccount: userId).ConfigureAwait(false);
        }
    }

    // The account's intents of one scope, each read again at its own address.
    private async IAsyncEnumerable<Versioned<ClosureJob>> IntentsAsync(string userId, string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(AuthStore.ClosureJobCollection, PlatformDirectory.Instance, "/accountId", userId, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(ClosureJson.Default.ClosureJob) is { } intent && intent.Scope == scope && intent.AccountId == userId
                && await platform.ClosureAsync(scope, intent.SubjectId, cancellationToken).ConfigureAwait(false) is { } current && current.Value.Same(intent)
                && current.Value.AccountId == userId)
            {
                yield return current;
            }
        }
    }

    private async Task CleanUpAsync(UserRecord user, CancellationToken cancellationToken)
    {
        // Every session, passcode, MFA and token check reads the user first, so what a failure leaves behind grants nothing.
        try
        {
            await foreach (var document in store.QueryAsync(AuthStore.SessionCollection, user.InstanceId, "/userId", user.Id, cancellationToken).ConfigureAwait(false))
            {
                if (document.Document.Deserialize(IdentityJson.Default.SessionRecord) is { } session && session.UserId == user.Id)
                {
                    await store.CompareAndDeleteAsync(AuthStore.SessionCollection, user.InstanceId, session.TokenHash, document.Version, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var (collection, key) in new[] { (AuthStore.PasscodeCollection, user.Id), (AuthStore.MfaCollection, user.Id) })
            {
                if (await store.GetAsync(collection, user.InstanceId, key, cancellationToken).ConfigureAwait(false) is { } document)
                {
                    await store.CompareAndDeleteAsync(collection, user.InstanceId, key, document.Version, cancellationToken).ConfigureAwait(false);
                }
            }

            // Refresh tokens, device codes and sign-in entries, then the sign-ins (authorizations) they belong to.
            await foreach (var token in tokens.FindBySubjectAsync(user.Id, cancellationToken).ConfigureAwait(false))
            {
                await IgnoreRaceAsync(tokens.DeleteAsync(token, cancellationToken)).ConfigureAwait(false);
            }

            await foreach (var authorization in authorizations.FindBySubjectAsync(user.Id, cancellationToken).ConfigureAwait(false))
            {
                await IgnoreRaceAsync(authorizations.DeleteAsync(authorization, cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up after an account deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    private static async Task IgnoreRaceAsync(ValueTask delete)
    {
        try
        {
            await delete.ConfigureAwait(false);
        }
        catch (OpenIddictExceptions.ConcurrencyException)
        {
            // Changed or deleted by a concurrent request; the user is already gone.
        }
    }
}
