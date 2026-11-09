using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

public sealed class EmailAdmission(AuthData data, TimeProvider clock)
{
    private const string Key = "cloudflare";
    private static readonly Meter Meter = new(AuthMetrics.MeterName);
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>("ts_auth.email_admission");
    private static readonly Gauge<long> Remaining = Meter.CreateGauge<long>("ts_auth.email_remaining");

    internal static readonly TimeSpan Window = TimeSpan.FromHours(25);

    internal async Task<bool> AcquireAsync(EmailMessage message, CancellationToken cancellationToken, string? attemptId = null)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var now = clock.GetUtcNow();
            var cutoff = now - Window;
            var purpose = message.Purpose;
            var tenant = message.Tenant;
            var state = await data.ReadAsync(async (db, token) =>
            {
                await db.EmailReservations.Where(item => item.At <= cutoff).ExecuteDeleteAsync(token).ConfigureAwait(false);
                return await db.EmailBudgets.Where(budget => budget.Key == Key)
                    .Select(budget => new
                    {
                        Budget = new Versioned<EmailBudgetRecord>(budget, EF.Property<string>(budget, AuthDbContext.VersionProperty)),
                        Total = db.EmailReservations.Count(),
                        Authentication = db.EmailReservations.Count(item => item.Purpose == EmailPurpose.Authentication),
                        Purpose = db.EmailReservations.Count(item => item.Purpose == purpose),
                        Tenant = db.EmailReservations.Count(item => item.Purpose != EmailPurpose.Authentication && item.Tenant == tenant),
                        Attempt = attemptId != null && db.EmailReservations.Any(item => item.AttemptId == attemptId),
                    }).SingleOrDefaultAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                await data.SaveAsync(Change.Insert(new EmailBudgetRecord(Key, DateTimeOffset.UnixEpoch)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            Observe(state.Total, state.Authentication);
            var critical = purpose == EmailPurpose.Authentication;
            var allowed = !state.Attempt && state.Budget.Value.PausedUntil <= now && state.Total < 1000 && (critical ||
                state.Total - state.Authentication < 500 && state.Purpose < (purpose == EmailPurpose.Test ? 20 : 100) && state.Tenant < 50);
            if (!allowed)
            {
                Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
                return false;
            }

            if (!await data.SaveAsync(
                [
                    Change.Update(state.Budget, state.Budget.Value),
                    Change.Insert(new EmailReservationRecord(AuthIds.New(), now, purpose, tenant, attemptId)),
                ], cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Observe(state.Total + 1, state.Authentication + (critical ? 1 : 0));
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
            return true;
        }

        return false;
    }

    private static void Observe(int total, int authentication)
    {
        Remaining.Record(Math.Max(0, 1000 - total), new KeyValuePair<string, object?>("pool", "total"));
        Remaining.Record(Math.Max(0, 500 - authentication), new KeyValuePair<string, object?>("pool", "auth_reserve"));
    }

    internal async Task PauseAsync(DateTimeOffset? reset, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var until = reset ?? (delay is { } relative ? now + relative : now + TimeSpan.FromMinutes(1));
        until = until <= now ? now + TimeSpan.FromMinutes(1) : until > now + Window ? now + Window : until;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var budget = await data.ReadAsync((db, token) => db.EmailBudgets.Where(budget => budget.Key == Key).WithVersion().SingleOrDefaultAsync(token), cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("Missing mail admission record.");
            if (budget.Value.PausedUntil >= until || await data.SaveAsync(Change.Update(budget, budget.Value with { PausedUntil = until }), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new InvalidOperationException("Mail provider pause could not be persisted.");
    }
}

public sealed record EmailBudgetRecord(string Key, DateTimeOffset PausedUntil);

public sealed record EmailReservationRecord(string Id, DateTimeOffset At, EmailPurpose Purpose, string Tenant, string? AttemptId = null);

public sealed class AdmittedEmailSender(EmailAdmission admission, CloudflareEmailSender provider, ILogger<AdmittedEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken).ConfigureAwait(false);

    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        var outcome = EmailDeliveryOutcome.Retryable;
        try
        {
            if (!await admission.AcquireAsync(message, cancellationToken).ConfigureAwait(false))
            {
                AuthMetrics.Email("capacity_refused");
                return EmailDeliveryOutcome.Retryable;
            }
            outcome = EmailDeliveryOutcome.Unknown;
            var result = await provider.DeliverResultAsync(recipient, message, cancellationToken).ConfigureAwait(false);
            outcome = result.Outcome;
            if (result.Outcome == EmailDeliveryOutcome.Retryable)
            {
                AuthMetrics.Email("provider_exhausted");
                await admission.PauseAsync(result.Reset, result.ResetDelay, cancellationToken).ConfigureAwait(false);
            }
            return result.Outcome;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthMetrics.Email("admission_unavailable");
            logger.ZLogError($"Email admission failed ({error.GetType().Name:@FailureType})");
            return outcome;
        }
    }
}
