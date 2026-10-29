using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Mcp
{
    // A public registration has no account owner. ClosedAt is terminal protocol state.
    public sealed record McpRegistration(string ClientId, string Name, string[] RedirectUris,
        DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
        DateTimeOffset? FirstSignInCompletedAt = null, DateTimeOffset? ClosedAt = null);

    public static class McpRegistrations
    {
        public static McpRegistration Create(string clientId, string name, string[] redirectUris, DateTimeOffset now)
        {
            var registration = new McpRegistration(clientId, name, [.. redirectUris], now, now);
            Validate(registration, clientId);
            return registration;
        }

        // Consent/discovery do not record activity. An expired read only closes the row.
        public static Task<McpRegistration?> ReadActiveAsync(IDashboardState state, TimeProvider time, string clientId, CancellationToken cancellationToken)
        {
            return ReadAsync(state, time, clientId, Activity.None, cancellationToken);
        }

        // Call only after a successful fresh ts-auth callback, never on consent POST.
        public static Task<McpRegistration?> CompleteSignInAsync(IDashboardState state, TimeProvider time, string clientId, CancellationToken cancellationToken)
        {
            return ReadAsync(state, time, clientId, Activity.SignIn, cancellationToken);
        }

        // Successful issuance or fully validated bound bearer; cannot complete sign-in.
        public static Task<McpRegistration?> TouchAsync(IDashboardState state, TimeProvider time, string clientId, CancellationToken cancellationToken)
        {
            return ReadAsync(state, time, clientId, Activity.Authenticated, cancellationToken);
        }

        private enum Activity { None, SignIn, Authenticated }

        private static async Task<McpRegistration?> ReadAsync(IDashboardState state, TimeProvider time, string clientId, Activity activity, CancellationToken cancellationToken)
        {
            if (!Canonical(clientId))
            {
                return null;
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                // Natural key goes to the provider exactly once; never hash it here.
                var document = await state.GetAsync(McpOAuth.ClientCollection, clientId, cancellationToken);
                if (document is null)
                {
                    return null;
                }

                if (document.Id != clientId || document.State != "value" || string.IsNullOrWhiteSpace(document.Version))
                {
                    throw new IOException("MCP registration state identity is invalid.");
                }

                var current = document.Document.Deserialize<McpRegistration>() ?? throw new JsonException("Invalid MCP registration.");
                Validate(current, clientId);
                if (current.ClosedAt is not null)
                {
                    return null;
                }
                // Sample after each fresh read, including every retry; never renew an expired row.
                var now = time.GetUtcNow();
                if (current.CreatedAt > now || current.LastActivityAt > now)
                {
                    throw new IOException("MCP registration activity is in the future.");
                }

                var expired = current.FirstSignInCompletedAt is null
                    ? now - current.CreatedAt >= TimeSpan.FromHours(24)
                    : now - current.LastActivityAt >= TimeSpan.FromDays(30);
                if (!expired && activity == Activity.None)
                {
                    return current;
                }

                if (!expired && activity == Activity.Authenticated && current.FirstSignInCompletedAt is null)
                {
                    return null;
                }

                var latest = now > current.LastActivityAt ? now : current.LastActivityAt;
                var next = expired ? current with { ClosedAt = latest } : current with
                {
                    FirstSignInCompletedAt = current.FirstSignInCompletedAt ?? latest,
                    LastActivityAt = latest
                };
                try
                {
                    await state.PutAsync(McpOAuth.ClientCollection, clientId, JsonSerializer.SerializeToElement(next),
                        TerraBasePrecondition.Matches(document.Version), cancellationToken);
                    return expired ? null : next;
                }
                catch (TerraBasePreconditionFailedException) { }
            }
            throw new IOException("MCP registration changed concurrently.");
        }

        // Listings carry provider IDs, not natural client IDs. Only a fresh, validated
        // natural-key read authorizes deletion; a concurrent sign-in wins through CAS.
        internal static async Task ReclaimUnfinishedAsync(IDashboardState state, TimeProvider time,
            TerraBaseDocument listed, CancellationToken cancellationToken)
        {
            if (listed.State != "value")
            {
                return;
            }

            var candidate = listed.Document.Deserialize<McpRegistration>() ?? throw new JsonException("Invalid MCP registration.");
            if (!Canonical(candidate.ClientId))
            {
                return; // Legacy clients are outside this policy.
            }

            Validate(candidate, candidate.ClientId);
            if (listed.Id != TerraBaseDashboardState.DocumentId(McpOAuth.ClientCollection, candidate.ClientId))
            {
                throw new IOException("MCP registration listing identity is invalid.");
            }

            var fresh = await state.GetAsync(McpOAuth.ClientCollection, candidate.ClientId, cancellationToken);
            if (fresh is null)
            {
                return;
            }

            if (fresh.Id != candidate.ClientId || fresh.State != "value" || string.IsNullOrWhiteSpace(fresh.Version))
            {
                throw new IOException("MCP registration state identity is invalid.");
            }

            var current = fresh.Document.Deserialize<McpRegistration>() ?? throw new JsonException("Invalid MCP registration.");
            Validate(current, candidate.ClientId);
            var now = time.GetUtcNow();
            if (current.CreatedAt > now || current.LastActivityAt > now)
            {
                throw new IOException("MCP registration activity is in the future.");
            }

            if (current.FirstSignInCompletedAt is not null || now - current.CreatedAt < TimeSpan.FromHours(24))
            {
                return;
            }

            try { await state.DeleteAsync(McpOAuth.ClientCollection, current.ClientId, fresh.Version, cancellationToken); }
            catch (TerraBasePreconditionFailedException) { } // A newer lifecycle transition must survive.
        }

        private static bool Canonical(string? clientId)
        {
            return clientId is { Length: 26 } && TerraScaleIds.IsValid(clientId) && clientId == clientId.ToUpperInvariant();
        }

        private static void Validate(McpRegistration value, string clientId)
        {
            if (!Canonical(value.ClientId) || value.ClientId != clientId ||
                string.IsNullOrWhiteSpace(value.Name) || value.Name.EnumerateRunes().Count() > 64 || value.Name.Any(char.IsControl) ||
                value.RedirectUris is not { Length: > 0 and <= 10 } || value.RedirectUris.Any(string.IsNullOrWhiteSpace) ||
                value.CreatedAt == default || value.LastActivityAt < value.CreatedAt ||
                (value.FirstSignInCompletedAt is { } first ? first < value.CreatedAt || first > value.LastActivityAt : value.LastActivityAt != value.CreatedAt) ||
                value.ClosedAt is { } closed && closed < value.LastActivityAt)
            {
                throw new IOException("MCP registration lifecycle is invalid.");
            }
        }
    }
}
