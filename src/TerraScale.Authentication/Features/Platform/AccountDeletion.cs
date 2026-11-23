using Dapper;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public readonly union DeletionStatusResult(bool, AuthNotFound, AuthConflict);

public readonly union DeletionHistoryResult(AccountDeletionHistory, AuthNotFound, AuthConflict);

public readonly union DeletionReceiptLookup(AccountDeletionReceipt, AuthNotFound);

public sealed class AccountDeletion(
    PlatformDirectory platform,
    Closures closures,
    Users users,
    AuthData data,
    StepUpProofs proofs,
    TimeProvider clock,
    IOpenIddictAuthorizationStore<OidcAuthorization> authorizations,
    IOpenIddictTokenStore<OidcToken> tokens,
    ILogger<AccountDeletion> logger)
{
    public async Task<bool> DeleteAsync(UserRecord user, CancellationToken cancellationToken, string? receiptHash = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var intent = await platform.CommitAsync(async () =>
        {
            var current = await platform.ClosureAsync("account", user.Id, cancellationToken);
            var created = ClosureJob.New(PlatformDirectory.Instance, "account", user.Id, null, user.Id, clock);
            var closure = current?.Value ?? created.Job;
            List<Change> operations = [.. await proofs.RequirementsAsync(user, cancellationToken) ?? throw StepUpProofs.Required()];
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            if (current is null)
            {
                var (sole, _) = await platform.AccountScopeAsync(user.Id, cancellationToken);
                candidates.UnionWith(sole);
                operations.Add(created.Create);
                operations.Add(Change.Update(user));
                foreach (var organizationId in sole)
                {
                    operations.Add(await platform.OwnerAssertionAsync(user.Id, organizationId, cancellationToken) ?? throw ApiException.Unavailable());
                    operations.Add(ClosureJob.New(PlatformDirectory.Instance, "organization", organizationId, organizationId, user.Id, clock, user.Id).Create);
                    operations.Add(await this.TouchAsync(organizationId, cancellationToken));
                }
            }
            else
            {
                operations.Add(Change.Update(current, closure));
            }

            if (receiptHash is not null)
            {
                var instance = user.InstanceId;
                var existing = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AccountDeletionReceipt>(
                    "SELECT * FROM account_deletion_receipts WHERE instance_id = @instance AND token_hash = @receiptHash", new { instance, receiptHash }), cancellationToken);
                if (existing is not null)
                {
                    var receipt = existing;
                    if (receipt.SubjectId != user.Id || receipt.Generation != closure.Generation || receipt.ExpiresAt <= clock.GetUtcNow())
                    {
                        throw PlatformDirectory.Conflict("deletion_refused", "The receipt does not identify this account closure.");
                    }
                }
                else
                {
                    if (current is not null)
                    {
                        await foreach (var owned in this.IntentsAsync(user.Id, "organization", cancellationToken))
                        {
                            candidates.Add(owned.Value.SubjectId);
                        }
                    }

                    var receipt = new AccountDeletionReceipt(user.InstanceId, receiptHash, user.Id, closure.Generation!, clock.GetUtcNow() + DeletionJob.CredentialBound, [.. candidates]);
                    operations.Add(Change.Insert(receipt));
                }
            }

            return new PlatformDirectory.Plan<ClosureJob>(operations, closure);
        }, cancellationToken);
        var job = await platform.ClosureAsync("account", user.Id, cancellationToken);
        if (job is null || !job.Value.Same(intent))
        {
            return await this.CompletedAsync(user.Id, intent.Generation!, cancellationToken)
                ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
        }

        return await this.AdvanceAsync(job, cancellationToken);
    }

    public async Task<DeletionStatusResult> StatusAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken) =>
        await this.ReadReceiptAsync(receiptHash, organizationId, cancellationToken) switch
        {
            AuthNotFound failure => failure,
            AccountDeletionReceipt found => await this.StatusAsync(found, organizationId, cancellationToken),
        };

    private async Task<DeletionStatusResult> StatusAsync(AccountDeletionReceipt found, string? organizationId, CancellationToken cancellationToken)
    {
        var job = await platform.ClosureAsync("account", found.SubjectId, cancellationToken);
        if (await this.CompletedAsync(found.SubjectId, found.Generation, cancellationToken))
        {
            if (organizationId is null)
            {
                return true;
            }

            var key = DeletionJob.Key(PlatformDirectory.Instance, "organization", organizationId);
            var proof = await this.JobAsync(key, cancellationToken);
            return proof is not null && proof.Id == key && proof.InstanceId == PlatformDirectory.Instance && proof.Scope == "organization" && proof.SubjectId == organizationId;
        }

        if (job?.Value.Generation == found.Generation)
        {
            return false;
        }

        return DeletionRefused();
    }

    internal async Task<DeletionHistoryResult> HistoryAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken) =>
        await this.StatusAsync(receiptHash, organizationId, cancellationToken) switch
        {
            true => await this.HistoryOfAsync(receiptHash, organizationId, cancellationToken),
            false => PlatformDirectory.NotFoundFailure(),
            AuthNotFound failure => failure,
            AuthConflict failure => failure,
        };

    private async Task<DeletionHistoryResult> HistoryOfAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken) =>
        await this.ReadReceiptAsync(receiptHash, organizationId, cancellationToken) switch
        {
            AuthNotFound failure => failure,
            AccountDeletionReceipt found => await this.HistoryOfAsync(found, organizationId, cancellationToken),
        };

    private async Task<DeletionHistoryResult> HistoryOfAsync(AccountDeletionReceipt found, string? organizationId, CancellationToken cancellationToken)
    {
        var scope = organizationId is null ? "account" : "organization";
        var subject = organizationId ?? found.SubjectId;
        var key = DeletionJob.Key(PlatformDirectory.Instance, scope, subject);
        var job = await this.JobAsync(key, cancellationToken);
        return job is not null && job.Id == key && job.InstanceId == PlatformDirectory.Instance && job.Scope == scope && job.SubjectId == subject
            && (organizationId is not null || job.ClosureGeneration == found.Generation) && job.ErasureId is { } erasure && PlatformDirectory.IsId(erasure)
                ? new AccountDeletionHistory(erasure, job.RequestedAt, job.AuthLogicalVerifiedAt, job.HistoryStatus, job.HistoryCompletedAt)
                : PlatformDirectory.NotFoundFailure();
    }

    private static AuthConflict DeletionRefused() => new("No completion proof exists for this closure generation.", "deletion_refused");

    private async Task<DeletionReceiptLookup> ReadReceiptAsync(string receiptHash, string? organizationId, CancellationToken cancellationToken)
    {
        var platform = PlatformDirectory.Instance;
        var receipt = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AccountDeletionReceipt>(
            "SELECT * FROM account_deletion_receipts WHERE instance_id = @platform AND token_hash = @receiptHash", new { platform, receiptHash }), cancellationToken);
        return receipt is not null && receipt.InstanceId == PlatformDirectory.Instance && receipt.TokenHash == receiptHash && receipt.ExpiresAt > clock.GetUtcNow()
            && (organizationId is null || receipt.OrganizationIds.Contains(organizationId, StringComparer.Ordinal))
                ? receipt : PlatformDirectory.NotFoundFailure();
    }

    private async Task<bool> CompletedAsync(string userId, string generation, CancellationToken cancellationToken)
    {
        var key = DeletionJob.Key(PlatformDirectory.Instance, "account", userId);
        return await this.JobAsync(key, cancellationToken) is { } proof
            && proof.Id == key && proof.InstanceId == PlatformDirectory.Instance && proof.Scope == "account" && proof.SubjectId == userId && proof.ClosureGeneration == generation;
    }

    internal async Task<bool> AdvanceAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var userId = job.Value.SubjectId;
        if (await users.GetAsync(PlatformDirectory.Instance, userId, cancellationToken) is not { } user)
        {
            await data.SaveAsync(Change.Delete(job), cancellationToken);
            return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken)
                ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
        }

        if (job.Value.CommittedAt is null && await platform.IsClosureCancellingAsync(job.Value, cancellationToken))
        {
            await this.CancelAsync(job, "refused", cancellationToken);
            return false;
        }

        IReadOnlyList<string> sole, shared;
        try
        {
            (sole, shared) = await platform.AccountScopeAsync(userId, cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
        {
            await this.RefuseAsync(job, error, cancellationToken);
            throw;
        }

        await foreach (var stale in this.IntentsAsync(userId, "organization", cancellationToken))
        {
            if (!sole.Contains(stale.Value.SubjectId))
            {
                await platform.CancelClosureAsync(stale, cancellationToken);
            }
        }

        foreach (var organizationId in sole)
        {
            if (await platform.ClosureAsync("organization", organizationId, cancellationToken) is not { Value.AccountId: { } owner } || owner != userId)
            {
                await platform.CommitAsync(async () => new PlatformDirectory.Plan<bool>(
                [
                    await platform.OwnerAssertionAsync(userId, organizationId, cancellationToken) ?? throw ApiException.Unavailable(),
                    Change.Assert(await platform.ClosureAsync("account", userId, cancellationToken) ?? throw ApiException.Unavailable()),
                    ClosureJob.New(PlatformDirectory.Instance, "organization", organizationId, organizationId, userId, clock, userId).Create,
                    await this.TouchAsync(organizationId, cancellationToken),
                ], true), cancellationToken);
            }
        }

        var pending = false;
        foreach (var organizationId in sole)
        {
            foreach (var projectId in await platform.ProjectIdsAsync(organizationId, cancellationToken))
            {
                try
                {
                    var project = await platform.RequestProjectClosureAsync(userId, organizationId, projectId, userId, cancellationToken);
                    pending |= !await closures.AdvanceAsync(project, cancellationToken);
                }
                catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
                {
                }
                catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
                {
                    await this.RefuseAsync(job, error, cancellationToken);
                    throw;
                }
            }
        }

        if (pending)
        {
            await closures.DeferAsync(job, "project_closure_pending", cancellationToken);
            return false;
        }

        for (var attempt = 0; job.Value.CommittedAt is null; attempt++)
        {
            var committed = job.Value with { CommittedAt = clock.GetUtcNow() };
            if (await platform.CommitAccountClosureAsync(job, committed, cancellationToken) is { } version)
            {
                job = new(committed, version);
                break;
            }

            if (await platform.ClosureAsync("account", userId, cancellationToken) is not { } reread || !reread.Value.Same(job.Value))
            {
                return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken)
                    ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
            }

            job = reread;
            if (job.Value.CommittedAt is null && await platform.IsClosureCancellingAsync(job.Value, cancellationToken))
            {
                await this.CancelAsync(job, "refused", cancellationToken);
                return false;
            }

            if (attempt == 3 && job.Value.CommittedAt is null)
            {
                throw ApiException.Unavailable();
            }
        }

        foreach (var organizationId in shared)
        {
            try
            {
                await platform.RemoveAsync(userId, organizationId, userId, cancellationToken);
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
            {
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status409Conflict)
            {
                await this.RefuseAsync(job, error, cancellationToken);
                throw;
            }
        }

        foreach (var organizationId in sole)
        {
            try
            {
                await platform.DeleteOrganizationAsync(userId, organizationId, closingAccount: true, cancellationToken);
            }
            catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
            {
            }

            if (await platform.ClosureAsync("organization", organizationId, cancellationToken) is { } intent)
            {
                await platform.CancelClosureAsync(intent, cancellationToken);
            }
        }

        await users.DeleteAsync(user.InstanceId, user.Id, job.Value.Generation, cancellationToken);
        await this.CleanUpAsync(user, cancellationToken);
        await data.SaveAsync(Change.Delete(job), cancellationToken);
        return await this.CompletedAsync(userId, job.Value.Generation!, cancellationToken)
            ? true : throw PlatformDirectory.Conflict("deletion_refused", "No completion proof exists for this closure generation.");
    }

    private async Task RefuseAsync(Versioned<ClosureJob> job, ApiException error, CancellationToken cancellationToken)
    {
        var code = error.Code is { Length: <= 64 } known ? known : "refused";
        if (job.Value.CommittedAt is not null)
        {
            await closures.DeferAsync(job, code, cancellationToken);
            return;
        }

        await this.CancelAsync(job, code, cancellationToken);
    }

    private async Task CancelAsync(Versioned<ClosureJob> job, string code, CancellationToken cancellationToken)
    {
        var current = job;
        for (var attempt = 0; ; attempt++)
        {
            if (current.Value.CommittedAt is not null)
            {
                await closures.DeferAsync(current, code, cancellationToken);
                return;
            }

            if (await platform.CancelClosureAsync(current, cancellationToken))
            {
                break;
            }

            if (attempt == 3)
            {
                throw ApiException.Unavailable();
            }

            if (await platform.ClosureAsync("account", job.Value.SubjectId, cancellationToken) is not { } reread || !reread.Value.Same(job.Value))
            {
                return;
            }

            current = reread;
        }

        var userId = job.Value.SubjectId;
        await foreach (var project in this.IntentsAsync(userId, "project", cancellationToken))
        {
            _ = await closures.DetachAsync(project, cancellationToken);
        }

        await foreach (var intent in this.IntentsAsync(userId, "organization", cancellationToken))
        {
            await platform.CancelClosureAsync(intent, cancellationToken, unlessAccount: userId);
        }
    }

    private async IAsyncEnumerable<Versioned<ClosureJob>> IntentsAsync(string userId, string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var instance = PlatformDirectory.Instance;
        var intents = await data.ReadAsync(connection => connection.QueryAsync<ClosureJob>(
            "SELECT * FROM closure_jobs WHERE instance_id = @instance AND account_id = @userId AND scope = @scope", new { instance, userId, scope }), cancellationToken);
        foreach (var intent in intents)
        {
            if (await platform.ClosureAsync(scope, intent.SubjectId, cancellationToken) is { } current && current.Value.Same(intent)
                && current.Value.AccountId == userId)
            {
                yield return current;
            }
        }
    }

    private async Task<Change> TouchAsync(string organizationId, CancellationToken cancellationToken) =>
        await platform.OrganizationAsync(organizationId, cancellationToken) is { } organization
            ? Change.Update(organization, organization.Value)
            : throw ApiException.Unavailable();

    private Task<DeletionJob?> JobAsync(string key, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<DeletionJob>("SELECT * FROM deletion_jobs WHERE id = @key", new { key }), cancellationToken);

    private async Task CleanUpAsync(UserRecord user, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var token in tokens.FindBySubjectAsync(user.Id, cancellationToken))
            {
                await IgnoreRaceAsync(tokens.DeleteAsync(token, cancellationToken));
            }

            await foreach (var authorization in authorizations.FindBySubjectAsync(user.Id, cancellationToken))
            {
                await IgnoreRaceAsync(authorizations.DeleteAsync(authorization, cancellationToken));
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
            await delete;
        }
        catch (OpenIddictExceptions.ConcurrencyException)
        {
        }
    }
}
