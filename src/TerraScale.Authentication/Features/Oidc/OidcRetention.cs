using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Bounded, conditional cleanup after the absolute lifetime of every supported descendant.</summary>
public sealed class OidcRetention(AuthStore store, TerraBaseDatabase database, TimeProvider clock)
{
    /// <summary>One primary-key page per collection per pass; no filtered full-collection queries.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Proof entries survive thirty-one days from creation: beyond device approval/code redemption, a project's
    /// thirty-day absolute refresh family and the final five-minute JWT. This is eligibility, not an erasure deadline.
    /// </summary>
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromDays(31);

    // Scheduling hints only. Restart or concurrent sweeps may repeat a page; conditional deletes remain authoritative.
    private string? tokenCursor;
    private string? authorizationCursor;

    /// <summary>Examines at most one token page, optionally restricted to one tenant; returns successful deletes.</summary>
    public async ValueTask<long> PruneTokensAsync(DateTimeOffset threshold, string? tenant, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var page = await this.ReadPageAsync(tokens: true, cancellationToken).ConfigureAwait(false);
        long deleted = 0;
        foreach (var document in page.Items)
        {
            if (Read(document, OidcJson.Default.OidcToken) is not { Id: { } id, InstanceId: { } instance, CreationDate: { } created, ExpirationDate: { } expires } token
                || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(instance)
                || token.ReferenceId is { } handle && string.IsNullOrWhiteSpace(handle)
                || tenant is not null && tenant != instance || created > threshold || created > now - ProofLifetime || expires > now
                || token.Type is not (TokenTypeIdentifiers.RefreshToken or TokenTypeIdentifiers.Private.AuthorizationCode or TokenTypeIdentifiers.Private.DeviceCode or TokenTypeIdentifiers.Private.UserCode)
                || document.Id != AuthStore.NaturalKeyId(instance, AuthStore.OidcTokenCollection, id))
            {
                continue;
            }

            List<StoreOperation> operations = [AuthStore.Delete(AuthStore.OidcTokenCollection, instance, id, document.Version)];
            if (token.ReferenceId is { } reference)
            {
                await AddReleaseAsync("oidc_token_reference", reference, id).ConfigureAwait(false);
            }

            // Only this token's redemption winner. Never release inherited session, user-state or authorization proofs.
            await AddReleaseAsync("credential:oidc", id, null).ConfigureAwait(false);
            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
            {
                deleted++;
            }

            async Task AddReleaseAsync(string kind, string key, string? owner)
            {
                if (await store.FindUniqueAsync(instance, kind, key, cancellationToken).ConfigureAwait(false) is { } claim
                    && (owner is null || owner == claim.OwnerId))
                {
                    operations.Add(AuthStore.Release(instance, kind, key, claim.Version));
                }
            }
        }

        this.tokenCursor = page.NextCursor;
        return deleted;
    }

    /// <summary>Examines one authorization page; retains permanent grants and records without a known creation time.</summary>
    public async ValueTask<long> PruneAuthorizationsAsync(DateTimeOffset threshold, string? tenant, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - ProofLifetime;
        var page = await this.ReadPageAsync(tokens: false, cancellationToken).ConfigureAwait(false);
        long deleted = 0;
        foreach (var document in page.Items)
        {
            if (Read(document, OidcJson.Default.OidcAuthorization) is { Id: { } id, InstanceId: { } instance, CreationDate: { } created, Type: AuthorizationTypes.AdHoc }
                && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(instance)
                && (tenant is null || tenant == instance) && created <= threshold && created <= cutoff
                && document.Id == AuthStore.NaturalKeyId(instance, AuthStore.OidcAuthorizationCollection, id)
                && await store.CompareAndDeleteAsync(AuthStore.OidcAuthorizationCollection, instance, id, document.Version, cancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        this.authorizationCursor = page.NextCursor;
        return deleted;
    }

    private async Task<TerraBaseDocumentPage> ReadPageAsync(bool tokens, CancellationToken cancellationToken)
    {
        var collection = tokens ? AuthStore.OidcTokenCollection : AuthStore.OidcAuthorizationCollection;
        var cursor = tokens ? this.tokenCursor : this.authorizationCursor;
        try
        {
            return await database.ListDocumentsAsync(collection, PageSize, cursor, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBaseException error) when (cursor is not null && error.ErrorCode == "invalid_cursor")
        {
            // A changed collection cursor contract invalidates only the scheduling hint, never a proof.
            return await database.ListDocumentsAsync(collection, PageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static T? Read<T>(TerraBaseDocument document, JsonTypeInfo<T> metadata) where T : class
    {
        try
        {
            return document.Document.Deserialize(metadata);
        }
        catch (JsonException)
        {
            // A malformed record supplies no lifetime proof. Retain it without blocking the rest of the page.
            return null;
        }
    }
}
