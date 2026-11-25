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
        Pause,
        Total,
        Shared,
        Tenant,
        TenantPurpose,
        Overflow,
        TenantAuthentication,
    }

    internal async Task<bool> AcquireAsync(EmailMessage message, CancellationToken cancellationToken, string? attemptId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var decision = await this.DecideAsync(message.Purpose, message.Tenant, attemptId, cancellationToken);
            var admitted = decision switch
            {
                EmailAdmitted granted => granted,
                EmailRefused => null,
            };
            if (admitted is null)
            {
                return false;
            }

            if (await data.SaveAsync(admitted.Changes, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    internal Task<bool> ReservedAsync(string reservationId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM email_reservations WHERE id = @reservationId)", new { reservationId }), cancellationToken);

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
        var binding = Binding(global.Value, counts, purpose, tenant, now).ToList();
        if (binding.Count > 0)
        {
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
            List<TimeSpan> waits = [];
            foreach (var (quota, excess) in binding)
            {
                waits.Add(await this.WaitAsync(quota, excess, global.Value, purpose, tenant, now, cancellationToken));
            }

            return new EmailRefused(waits.Max());
        }

        var reservation = new EmailReservationRecord(AuthIds.New(), now, purpose, tenant, attemptId);
        Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
        return new EmailAdmitted(reservation.Id, [Change.Update(global, global.Value), Change.Insert(reservation)]);
    }

    private static IEnumerable<(Quota Quota, int Excess)> Binding(EmailBudgetRecord global, AdmissionCounts counts, EmailPurpose purpose, string tenant, DateTimeOffset now)
    {
        if (counts.Attempt)
        {
            yield return (Quota.Attempt, 0);
        }

        if (global.PausedUntil > now)
        {
            yield return (Quota.Pause, 0);
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

    private async Task<TimeSpan> WaitAsync(Quota quota, int excess, EmailBudgetRecord global, EmailPurpose purpose, string tenant, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var authentication = (long)EmailPurpose.Authentication;
        var purposeValue = (long)purpose;
        var freedAt = quota switch
        {
            Quota.Attempt => now + AttemptRetry - Window,
            Quota.Pause => global.PausedUntil - Window,
            Quota.Overflow => await this.OverflowFreedAsync(excess, cancellationToken),
            Quota.Total => await this.NthOldestAsync("SELECT at FROM email_reservations ORDER BY at LIMIT 1 OFFSET @excess",
                excess, authentication, purposeValue, tenant, cancellationToken),
            Quota.Shared => await this.NthOldestAsync("SELECT at FROM email_reservations WHERE purpose <> @authentication ORDER BY at LIMIT 1 OFFSET @excess",
                excess, authentication, purposeValue, tenant, cancellationToken),
            Quota.Tenant => await this.NthOldestAsync("SELECT at FROM email_reservations WHERE purpose <> @authentication AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                excess, authentication, purposeValue, tenant, cancellationToken),
            Quota.TenantPurpose => await this.NthOldestAsync("SELECT at FROM email_reservations WHERE purpose = @purposeValue AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                excess, authentication, purposeValue, tenant, cancellationToken),
            Quota.TenantAuthentication => await this.NthOldestAsync(
                "SELECT at FROM email_reservations WHERE purpose = @authentication AND tenant = @tenant ORDER BY at LIMIT 1 OFFSET @excess",
                excess, authentication, purposeValue, tenant, cancellationToken),
            _ => throw new UnreachableException(),
        };
        return freedAt is { } at && at + Window > now ? at + Window - now : MinimumWait;
    }

    private async Task<DateTimeOffset?> NthOldestAsync(string sql, int excess, long authentication, long purposeValue, string tenant, CancellationToken cancellationToken) =>
        (await data.ReadAsync(connection => connection.QueryAsync<ReservationTime>(sql, new { excess, authentication, purposeValue, tenant }), cancellationToken))
            .Select(row => (DateTimeOffset?)row.At).FirstOrDefault();

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

    internal async Task PauseAsync(DateTimeOffset? reset, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var until = reset ?? (delay is { } relative ? now + relative : now + TimeSpan.FromMinutes(1));
        until = until <= now ? now + TimeSpan.FromMinutes(1) : until > now + Window ? now + Window : until;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var budget = await this.GlobalAsync(cancellationToken);
            if (budget.Value.PausedUntil >= until || await data.SaveAsync(Change.Update(budget, budget.Value with { PausedUntil = until }), cancellationToken))
            {
                return;
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

public readonly union EmailAdmissionDecision(EmailAdmitted, EmailRefused);

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
        var outcome = EmailDeliveryOutcome.Retryable;
        try
        {
            var reserved = message.AdmissionId is { } admitted && await admission.ReservedAsync(admitted, cancellationToken);
            if (reserved && await admission.PausedUntilAsync(cancellationToken) is not null)
            {
                AuthMetrics.Email("provider_paused");
                return EmailDeliveryOutcome.Retryable;
            }

            if (!reserved && !await admission.AcquireAsync(message, cancellationToken))
            {
                AuthMetrics.Email("capacity_refused");
                return EmailDeliveryOutcome.Retryable;
            }
            outcome = EmailDeliveryOutcome.Unknown;
            var result = await provider.DeliverResultAsync(recipient, message, cancellationToken);
            outcome = result.Outcome;
            if (result.Outcome == EmailDeliveryOutcome.Retryable)
            {
                AuthMetrics.Email("provider_exhausted");
                await admission.PauseAsync(result.Reset, result.ResetDelay, cancellationToken);
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
