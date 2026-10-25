using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Hosting;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

/// <summary>Durable provider-account admission, shared by every sending instance on the GRU storage authority.</summary>
public sealed class EmailAdmission(AuthStore store, TimeProvider clock)
{
    internal const string Collection = "email_admission";
    private const string Key = "cloudflare";
    private static readonly Meter Meter = new(AuthMetrics.MeterName);
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>("ts_auth.email_admission");
    private static readonly Gauge<long> Remaining = Meter.CreateGauge<long>("ts_auth.email_remaining");

    // Retain reservations beyond the 24-hour provider window, including the bounded provider request duration.
    internal static readonly TimeSpan Window = TimeSpan.FromHours(25);

    internal async Task<bool> AcquireAsync(EmailMessage message, CancellationToken cancellationToken, string? attemptId = null)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, Key, cancellationToken).ConfigureAwait(false);
            var state = Read(document);
            var now = clock.GetUtcNow();
            state.Reservations.RemoveAll(item => item.At <= now - Window);
            var purpose = message.Purpose;
            var critical = purpose == EmailPurpose.Authentication;
            var noncritical = state.Reservations.Count(item => item.Purpose != EmailPurpose.Authentication);
            var allowed = (attemptId is null || !state.Reservations.Any(item => item.AttemptId == attemptId)) && state.PausedUntil <= now && state.Reservations.Count < 1000 && (critical ||
                noncritical < 500 && state.Reservations.Count(item => item.Purpose == purpose) < (purpose == EmailPurpose.Test ? 20 : 100) &&
                state.Reservations.Count(item => item.Purpose != EmailPurpose.Authentication && item.Tenant == message.Tenant) < 50);
            Observe(state);
            if (!allowed)
            {
                Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "refused"));
                return false;
            }
            state.Reservations.Add(new(now, purpose, message.Tenant, attemptId));
            var record = JsonSerializer.SerializeToElement(state, EmailAdmissionJson.Default.EmailBudget);
            if (document is null)
            {
                try
                {
                    _ = await store.CreateAsync(Collection, OidcIssuer.InstanceId, Key, record, cancellationToken).ConfigureAwait(false);
                }
                catch (TerraBasePreconditionFailedException) { continue; }
            }
            else if (!await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, Key, record, document.Version, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            Observe(state);
            Decisions.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()), new KeyValuePair<string, object?>("outcome", "admitted"));
            return true;
        }
        return false;
    }

    private static EmailBudget Read(TerraBaseDocument? document)
    {
        if (document is null)
        {
            return new(OidcIssuer.InstanceId, [], default);
        }
        var state = document.Document.Deserialize(EmailAdmissionJson.Default.EmailBudget);
        return document.State == "value" && document.Id == AuthStore.NaturalKeyId(OidcIssuer.InstanceId, Collection, Key) &&
            state is { InstanceId: OidcIssuer.InstanceId, Reservations.Count: <= 1000 } ? state
            : throw new InvalidOperationException("Invalid mail admission record.");
    }

    private static void Observe(EmailBudget state)
    {
        Remaining.Record(Math.Max(0, 1000 - state.Reservations.Count), new KeyValuePair<string, object?>("pool", "total"));
        Remaining.Record(Math.Max(0, 500 - state.Reservations.Count(item => item.Purpose == EmailPurpose.Authentication)), new KeyValuePair<string, object?>("pool", "auth_reserve"));
    }

    internal async Task PauseAsync(DateTimeOffset? reset, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var until = reset ?? (delay is { } relative ? now + relative : now + TimeSpan.FromMinutes(1));
        until = until <= now ? now + TimeSpan.FromMinutes(1) : until > now + Window ? now + Window : until;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var document = await store.GetAsync(Collection, OidcIssuer.InstanceId, Key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Missing mail admission record.");
            var state = Read(document);
            if (state.PausedUntil >= until || await store.CompareAndPutAsync(Collection, OidcIssuer.InstanceId, Key,
                JsonSerializer.SerializeToElement(state with { PausedUntil = until }, EmailAdmissionJson.Default.EmailBudget), document.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        throw new InvalidOperationException("Mail provider pause could not be persisted.");
    }
}

internal sealed record EmailReservation(DateTimeOffset At, EmailPurpose Purpose, string Tenant, string? AttemptId = null);
internal sealed record EmailBudget(string InstanceId, List<EmailReservation> Reservations, DateTimeOffset PausedUntil);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmailBudget))]
internal sealed partial class EmailAdmissionJson : JsonSerializerContext;

/// <summary>Reserves capacity before sending; failed or ambiguous sends keep their reservation and are never resent here.</summary>
public sealed class AdmittedEmailSender(EmailAdmission admission, CloudflareEmailSender provider, ILogger<AdmittedEmailSender> logger) : IEmailSender
{
    /// <inheritdoc />
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
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
