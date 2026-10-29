using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E
{
    // Hosted approval is deliberate and test-only. Begin never marks a proof approved.
    internal sealed class TestStepUpGateway(TimeProvider time, Func<string, (string UserId, string Email)?> resolve) : IAuthStepUpGateway
    {
        internal sealed record Pending(string UserId, string AccessToken, string Purpose, string Audience, string ReturnUri, StepUpChallenge Challenge, bool Approved = false);
        private readonly ConcurrentDictionary<string, Pending> pending = new();
        private sealed record DeletionGrant(string UserId, string AccessToken, string Receipt, DateTimeOffset ExpiresAt);
        private readonly ConcurrentDictionary<string, DeletionGrant> grants = new();
        private int consumed;
        public int Consumed => Volatile.Read(ref consumed);
        public string? LastReceipt { get; private set; }
        public bool LoseConsumeAnswer { get; set; }
        public bool OmitDeletionAuthorization { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }

        public Task<StepUpChallenge> BeginAsync(SessionRecord session, string purpose, string audience, string returnUri, CancellationToken cancellationToken)
        {
            if (resolve(session.AccessToken)?.UserId != session.UserId)
            {
                throw StepUpFlow.Required();
            }

            var id = Guid.NewGuid().ToString("N");
            var challenge = new StepUpChallenge(id + ".synthetic-backend-proof", "https://auth.terrascale.tech/ui/terrascale/step-up/" + id, ExpiresAt ?? time.GetUtcNow().AddMinutes(5));
            pending[id] = new(session.UserId, session.AccessToken, purpose, audience, returnUri, challenge);
            return Task.FromResult(challenge);
        }

        public Task<string?> ConsumeAsync(SessionRecord session, string proof, string purpose, string audience, string? operationReceipt, CancellationToken cancellationToken)
        {
            var entry = pending.SingleOrDefault(pair => pair.Value.Challenge.Proof == proof);
            if (entry.Value is not { Approved: true } value || value.UserId != session.UserId ||
                resolve(value.AccessToken)?.UserId != value.UserId || resolve(session.AccessToken)?.UserId != value.UserId ||
                value.Purpose != purpose || value.Audience != audience ||
                value.Challenge.ExpiresAt <= time.GetUtcNow() || purpose == "delete_account" && operationReceipt is not { Length: 43 } || !pending.TryRemove(entry))
            {
                throw StepUpFlow.Required();
            }

            Interlocked.Increment(ref consumed);
            string? grant = null;
            if (purpose == "delete_account")
            {
                LastReceipt = operationReceipt;
                grant = Guid.NewGuid().ToString("N");
                grants[grant] = new(value.UserId, value.AccessToken, operationReceipt!, time.GetUtcNow().AddDays(15));
            }
            if (LoseConsumeAnswer)
            {
                throw new IOException("Synthetic lost consumption response.");
            }

            return Task.FromResult(OmitDeletionAuthorization ? null : grant);
        }

        public bool AuthorizeDeletion(SessionRecord session, string receipt, string authorization)
        {
            return grants.TryGetValue(authorization, out var grant) && grant.UserId == session.UserId && grant.Receipt == receipt &&
            grant.ExpiresAt > time.GetUtcNow() && resolve(grant.AccessToken)?.UserId == session.UserId && resolve(session.AccessToken)?.UserId == session.UserId;
        }

        public void Map(WebApplication app)
        {
            app.MapGet("/__test/step-up/{id}", async (HttpContext http, string id, DashboardSessions sessions) =>
        {
            var session = await sessions.ReadAsync(http);
            if (!pending.TryGetValue(id, out var value) || session?.UserId != value.UserId || http.Request.Query["approve"] != "yes")
            {
                return Results.StatusCode(403);
            }

            if (!pending.TryUpdate(id, value with { Approved = true }, value))
            {
                return Results.StatusCode(403);
            }

            return Results.Redirect(value.ReturnUri);
        });
        }
    }
}
