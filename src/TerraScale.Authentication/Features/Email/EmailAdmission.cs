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

    internal async Task<bool> AcquireAsync(EmailMessage message, CancellationToken cancellationToken, string? attemptId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var purpose = message.Purpose;
        var critical = purpose == EmailPurpose.Authentication;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var now = clock.GetUtcNow();
            if (await this.StateAsync(purpose, message.Tenant, attemptId, now, cancellationToken) is not { } state)
            {
                continue;
            }

            Observe(state.Counts.Total, state.Counts.Authentication);
            if (state.Counts.Attempt || !Allows(state, purpose, message.Tenant, now))
            {
                Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
                return false;
            }

            var reservation = Change.Insert(new EmailReservationRecord(AuthIds.New(), now, purpose, message.Tenant, attemptId));
            if (!await data.SaveAsync(critical ? [reservation] : [Change.Update(state.Partition, state.Partition.Value), reservation], cancellationToken))
            {
                continue;
            }

            Observe(state.Counts.Total + 1, state.Counts.Authentication + (critical ? 1 : 0));
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
            return true;
        }

        return false;
    }

    internal async Task<TimeSpan?> RefusalAsync(EmailPurpose purpose, string tenant, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var now = clock.GetUtcNow();
            if (await this.StateAsync(purpose, tenant, null, now, cancellationToken) is { } state)
            {
                return Allows(state, purpose, tenant, now) ? null
                    : state.Global.Value.PausedUntil > now ? state.Global.Value.PausedUntil - now
                    : state.Counts.Oldest is { } oldest ? oldest + Window - now
                    : Window;
            }
        }

        throw ApiException.Unavailable();
    }

    private async Task<AdmissionState?> StateAsync(EmailPurpose purpose, string tenant, string? attemptId, DateTimeOffset now, CancellationToken cancellationToken)
    {
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
                (SELECT MIN(at) FROM email_reservations WHERE tenant = @tenant) AS oldest,
                @attemptId IS NOT NULL AND EXISTS (SELECT 1 FROM email_reservations WHERE attempt_id = @attemptId) AS attempt
            """, new { cutoff, authentication, purposeValue, tenant, attemptId, TenantFairShare }), cancellationToken);
        var global = await this.BudgetAsync(Key, cancellationToken);
        var partitionKey = PartitionKey(tenant);
        var partition = purpose == EmailPurpose.Authentication ? global : await this.BudgetAsync(partitionKey, cancellationToken);
        if (global is null || partition is null)
        {
            await data.SaveAsync(Change.Insert(new EmailBudgetRecord(global is null ? Key : partitionKey, DateTimeOffset.UnixEpoch)), cancellationToken);
            return null;
        }

        return new AdmissionState(global, partition, counts);
    }

    private static bool Allows(AdmissionState state, EmailPurpose purpose, string tenant, DateTimeOffset now)
    {
        var counts = state.Counts;
        if (state.Global.Value.PausedUntil > now || counts.Total >= TotalLimit)
        {
            return false;
        }

        if (purpose == EmailPurpose.Authentication)
        {
            return tenant is OidcIssuer.InstanceId or DefaultTenant || counts.TenantAuthentication < TenantAuthenticationLimit;
        }

        return counts.Total - counts.Authentication < SharedLimit
            && counts.Tenant < TenantLimit
            && counts.TenantPurpose < (purpose == EmailPurpose.Test ? TestLimit : TenantLimit)
            && (counts.Tenant < TenantFairShare || counts.Overflow < OverflowLimit);
    }

    private static string PartitionKey(string tenant) => "tenant:" + tenant;

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
            var budget = await this.BudgetAsync(Key, cancellationToken) ?? throw new InvalidOperationException("Missing mail admission record.");
            if (budget.Value.PausedUntil >= until || await data.SaveAsync(Change.Update(budget, budget.Value with { PausedUntil = until }), cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("Mail provider pause could not be persisted.");
    }

    private async Task<Versioned<EmailBudgetRecord>?> BudgetAsync(string key, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<EmailBudgetRecord>(
            "SELECT * FROM email_budgets WHERE \"key\" = @key", new { key }), cancellationToken));

    internal sealed record AdmissionCounts(int Total, int Authentication, int TenantAuthentication, int Tenant, int TenantPurpose, int Overflow, DateTimeOffset? Oldest, bool Attempt);

    private sealed record AdmissionState(Versioned<EmailBudgetRecord> Global, Versioned<EmailBudgetRecord> Partition, AdmissionCounts Counts);
}

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
            if (!await admission.AcquireAsync(message, cancellationToken))
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
