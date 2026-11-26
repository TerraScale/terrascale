using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

public sealed class EmailAdmission(AuthData data, TimeProvider clock)
{
    private const string Key = "smtp";
    private const string DefaultTenant = "platform";
    private static readonly Meter Meter = new(AuthMetrics.MeterName);
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>("ts_auth.email_admission");
    private static readonly Gauge<long> Remaining = Meter.CreateGauge<long>("ts_auth.email_remaining");

    internal static void Publish()
    {
        foreach (var purpose in Enum.GetValues<EmailPurpose>())
        {
            Decisions.Add(0, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
            Decisions.Add(0, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
            Decisions.Add(0, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "held"));
        }
    }

    internal static readonly TimeSpan Window = TimeSpan.FromHours(25);

    internal const int TotalLimit = 1000;

    internal const int SharedLimit = 500;

    internal const int OverflowLimit = 250;

    internal const int TenantLimit = 50;

    internal const int TenantFairShare = 10;

    internal const int TenantAuthenticationLimit = 200;

    internal const int TestLimit = 20;

    private static readonly TimeSpan AttemptRetry = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private enum Quota
    {
        Attempt,
        Total,
        Shared,
        Tenant,
        TenantPurpose,
        Overflow,
        TenantAuthentication,
    }

    internal async Task<EmailAdmissionDecision> AcquireAsync(EmailMessage message, CancellationToken cancellationToken, string? attemptId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var decision = await this.DecideAsync(message.Purpose, message.Tenant, attemptId, cancellationToken);
            var changes = decision switch
            {
                EmailAdmitted granted => granted.Changes,
                EmailRefused or EmailPaused => null,
            };
            if (changes is null || await data.SaveAsync(changes, cancellationToken))
            {
                return decision;
            }
        }

        return new EmailRefused(MinimumWait);
    }

    internal async Task<bool> HeldAsync(string reservationId, EmailPurpose purpose, CancellationToken cancellationToken)
    {
        var held = await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM email_reservations WHERE id = @reservationId)", new { reservationId }), cancellationToken);
        if (held)
        {
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "held"));
        }

        return held;
    }

    internal async Task<EmailAdmissionDecision> DecideAsync(EmailPurpose purpose, string tenant, string? attemptId, CancellationToken cancellationToken)
    {
        var global = await this.GlobalAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var cutoff = now - Window;
        var authentication = (long)EmailPurpose.Authentication;
        var purposeValue = (long)purpose;
        var counts = await data.ReadAsync(connection => connection.QuerySingleAsync<AdmissionCounts>(
            """
            DELETE FROM email_reservations WHERE at <= @cutoff;
            SELECT (SELECT COUNT(*) FROM email_reservations) AS total,
                (SELECT COUNT(*) FROM email_reservations WHERE purpose = @authentication) AS authentication,
                (SELECT COUNT(*) FROM email_reservations WHERE purpose = @authentication AND tenant = @tenant) AS tenant_authentication,
                (SELECT COUNT(*) FROM email_reservations WHERE purpose <> @authentication AND tenant = @tenant) AS tenant,
                (SELECT COUNT(*) FROM email_reservations WHERE purpose = @purposeValue AND tenant = @tenant) AS tenant_purpose,
                (SELECT COALESCE(SUM(used - @TenantFairShare), 0) FROM (SELECT COUNT(*) AS used FROM email_reservations WHERE purpose <> @authentication GROUP BY tenant) AS usage
                    WHERE used > @TenantFairShare) AS overflow,
                @attemptId IS NOT NULL AND EXISTS (SELECT 1 FROM email_reservations WHERE attempt_id = @attemptId) AS attempt
            """, new { cutoff, authentication, purposeValue, tenant, attemptId, TenantFairShare }), cancellationToken);
        Observe(counts.Total, counts.Authentication);
        if (!counts.Attempt && global.Value.PausedUntil > now)
        {
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "paused"));
            return new EmailPaused(global.Value.PausedUntil);
        }

        var binding = Binding(counts, purpose, tenant).ToList();
        if (binding.Count > 0)
        {
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
            List<TimeSpan> waits = [];
            foreach (var (quota, excess) in binding)
            {
                waits.Add(await this.WaitAsync(quota, excess, counts, purpose, tenant, now, cancellationToken));
            }

            return new EmailRefused(waits.Max());
        }

        var reservation = new EmailReservationRecord(AuthIds.New(), now, purpose, tenant, attemptId);
        Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
        return new EmailAdmitted(reservation.Id, [Change.Update(global, global.Value), Change.Insert(reservation)]);
    }

    private static IEnumerable<(Quota Quota, int Excess)> Binding(AdmissionCounts counts, EmailPurpose purpose, string tenant)
    {
        if (counts.Attempt)
        {
            yield return (Quota.Attempt, 0);
        }

        if (counts.Total >= TotalLimit)
        {
            yield return (Quota.Total, counts.Total - TotalLimit);
        }

        if (purpose == EmailPurpose.Authentication)
        {
            if (tenant is not (OidcIssuer.InstanceId or DefaultTenant) && counts.TenantAuthentication >= TenantAuthenticationLimit)
            {
                yield return (Quota.TenantAuthentication, counts.TenantAuthentication - TenantAuthenticationLimit);
            }

            yield break;
        }

        if (counts.Total - counts.Authentication >= SharedLimit)
        {
            yield return (Quota.Shared, counts.Total - counts.Authentication - SharedLimit);
        }

        if (counts.Tenant >= TenantLimit)
        {
            yield return (Quota.Tenant, counts.Tenant - TenantLimit);
        }

        var purposeLimit = purpose == EmailPurpose.Test ? TestLimit : TenantLimit;
        if (counts.TenantPurpose >= purposeLimit)
        {
            yield return (Quota.TenantPurpose, counts.TenantPurpose - purposeLimit);
        }

        if (counts.Tenant >= TenantFairShare && counts.Overflow >= OverflowLimit)
        {
            yield return (Quota.Overflow, counts.Overflow - OverflowLimit);
        }
    }

    private async Task<TimeSpan> WaitAsync(Quota quota, int excess, AdmissionCounts counts, EmailPurpose purpose, string tenant, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authentication = (long)EmailPurpose.Authentication;
        var purposeValue = (long)purpose;
        var fairShareExcess = counts.Tenant - TenantFairShare;
        var freedAt = quota switch
        {
            Quota.Attempt => now + AttemptRetry - Window,
            Quota.Overflow => Earliest(await this.OverflowFreedAsync(excess, cancellationToken), await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations WHERE purpose <> @authentication AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @fairShareExcess",
                new { authentication, tenant, fairShareExcess }), cancellationToken)),
            Quota.Total => await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations ORDER BY at LIMIT 1 OFFSET @excess", new { excess }), cancellationToken),
            Quota.Shared => await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations WHERE purpose <> @authentication ORDER BY at LIMIT 1 OFFSET @excess", new { authentication, excess }), cancellationToken),
            Quota.Tenant => await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations WHERE purpose <> @authentication AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                new { authentication, tenant, excess }), cancellationToken),
            Quota.TenantPurpose => await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations WHERE purpose = @purposeValue AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                new { purposeValue, tenant, excess }), cancellationToken),
            Quota.TenantAuthentication => await this.FirstAsync(connection => connection.QueryAsync<ReservationTime>(
                "SELECT at FROM email_reservations WHERE purpose = @authentication AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                new { authentication, tenant, excess }), cancellationToken),
            _ => throw new UnreachableException(),
        };
        return freedAt is { } at && at + Window > now ? at + Window - now : MinimumWait;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is { } one && second is { } two ? (one < two ? one : two) : first ?? second;

    private async Task<DateTimeOffset?> FirstAsync(Func<DbConnection, Task<IEnumerable<ReservationTime>>> read, CancellationToken cancellationToken) =>
        (await data.ReadAsync(read, cancellationToken)).Select(row => (DateTimeOffset?)row.At).FirstOrDefault();

    private async Task<DateTimeOffset?> OverflowFreedAsync(int excess, CancellationToken cancellationToken)
    {
        var authentication = (long)EmailPurpose.Authentication;
        var rows = await data.ReadAsync(connection => connection.QueryAsync<TenantReservation>(
            """
            SELECT tenant, at FROM email_reservations WHERE purpose <> @authentication AND tenant IN (
                SELECT tenant FROM email_reservations WHERE purpose <> @authentication GROUP BY tenant HAVING COUNT(*) > @TenantFairShare)
            ORDER BY tenant, at
            """, new { authentication, TenantFairShare }), cancellationToken);
        return rows.GroupBy(row => row.Tenant, StringComparer.Ordinal)
            .SelectMany(group => group.Take(group.Count() - TenantFairShare))
            .Select(row => row.At)
            .Order()
            .Skip(excess)
            .Select(at => (DateTimeOffset?)at)
            .FirstOrDefault();
    }

    internal async Task<DateTimeOffset?> PausedUntilAsync(CancellationToken cancellationToken) =>
        await this.BudgetAsync(cancellationToken) is { Value.PausedUntil: var until } && until > clock.GetUtcNow() ? until : null;

    private async Task<Versioned<EmailBudgetRecord>> GlobalAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await this.BudgetAsync(cancellationToken) is { } global)
            {
                return global;
            }

            await data.SaveAsync(Change.Insert(new EmailBudgetRecord(Key, DateTimeOffset.UnixEpoch)), cancellationToken);
        }

        throw ApiException.Unavailable();
    }

    private static void Observe(int total, int authentication)
    {
        Remaining.Record(Math.Max(0, TotalLimit - total), new KeyValuePair<string, object?>("pool", "total"));
        Remaining.Record(Math.Max(0, TotalLimit - SharedLimit - authentication), new KeyValuePair<string, object?>("pool", "auth_reserve"));
    }

    internal async Task<DateTimeOffset> PauseAsync(DateTimeOffset? reset, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var until = reset ?? (delay is { } relative ? now + relative : now + TimeSpan.FromMinutes(1));
        until = until <= now ? now + TimeSpan.FromMinutes(1) : until > now + Window ? now + Window : until;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var budget = await this.GlobalAsync(cancellationToken);
            if (budget.Value.PausedUntil >= until)
            {
                return budget.Value.PausedUntil;
            }

            if (await data.SaveAsync(Change.Update(budget, budget.Value with { PausedUntil = until }), cancellationToken))
            {
                return until;
            }
        }

        throw new InvalidOperationException("Mail provider pause could not be persisted.");
    }

    private async Task<Versioned<EmailBudgetRecord>?> BudgetAsync(CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<EmailBudgetRecord>(
            "SELECT * FROM email_budgets WHERE \"key\" = @Key", new { Key }), cancellationToken));

    internal sealed record AdmissionCounts(int Total, int Authentication, int TenantAuthentication, int Tenant, int TenantPurpose, int Overflow, bool Attempt);

    internal sealed record ReservationTime(DateTimeOffset At);

    internal sealed record TenantReservation(string Tenant, DateTimeOffset At);
}

public sealed record EmailAdmitted(string ReservationId, IReadOnlyList<Change> Changes);

public sealed record EmailRefused(TimeSpan RetryAfter);

public readonly union EmailAdmissionDecision(EmailAdmitted, EmailRefused, EmailPaused);

public sealed record EmailBudgetRecord(string Key, DateTimeOffset PausedUntil)
{
    public string? Version { get; init; }
}

public sealed record EmailReservationRecord(string Id, DateTimeOffset At, EmailPurpose Purpose, string Tenant, string? AttemptId = null);

public sealed class AdmittedEmailSender(EmailAdmission admission, FailoverEmailSender provider, ILogger<AdmittedEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken);

    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        var delivery = await this.DeliverUnlessPausedAsync(recipient, message, cancellationToken);
        return Outcome(delivery);
    }

    public async Task<EmailDelivery> DeliverUnlessPausedAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var outcome = EmailDeliveryOutcome.Retryable;
        try
        {
            if (await admission.PausedUntilAsync(cancellationToken) is { } pausedUntil)
            {
                AuthMetrics.Email("provider_paused");
                return new EmailPaused(pausedUntil);
            }

            var reserved = message.AdmissionId is { } admitted && await admission.HeldAsync(admitted, message.Purpose, cancellationToken);
            if (!reserved)
            {
                var acquired = await admission.AcquireAsync(message, cancellationToken);
                if (Unsent(acquired) is { } refusal)
                {
                    AuthMetrics.Email(refusal.Metric);
                    return refusal.Delivery;
                }
            }

            outcome = EmailDeliveryOutcome.Unknown;
            var result = await provider.DeliverResultAsync(recipient, message, cancellationToken);
            outcome = result.Outcome;
            if (result.Outcome == EmailDeliveryOutcome.Retryable)
            {
                AuthMetrics.Email("provider_exhausted");
                return new EmailPaused(await admission.PauseAsync(result.Reset, result.ResetDelay, cancellationToken));
            }

            return new EmailSent(result.Outcome);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthMetrics.Email("admission_unavailable");
            logger.ZLogError($"Email admission failed ({error.GetType().Name:@FailureType})");
            return new EmailSent(outcome);
        }
    }

    private static (EmailDelivery Delivery, string Metric)? Unsent(EmailAdmissionDecision decision) => decision switch
    {
        EmailAdmitted => null,
        EmailRefused => (new EmailSent(EmailDeliveryOutcome.Retryable), "capacity_refused"),
        EmailPaused paused => (paused, "provider_paused"),
    };

    private static EmailDeliveryOutcome Outcome(EmailDelivery delivery) => delivery switch
    {
        EmailSent sent => sent.Outcome,
        EmailPaused => EmailDeliveryOutcome.Retryable,
    };
}
