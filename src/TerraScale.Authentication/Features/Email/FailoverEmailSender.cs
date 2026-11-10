using TerraScale.Authentication.Hosting;
using ZLogger;

namespace TerraScale.Authentication.Features.Email;

public interface IEmailProvider
{
    Task<EmailProviderResult> DeliverResultAsync(string recipient, EmailMessage message, CancellationToken cancellationToken);
}

public sealed class FailoverEmailSender(IReadOnlyList<IEmailProvider> providers, ILogger<FailoverEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) =>
        _ = await this.DeliverAsync(recipient, message, cancellationToken).ConfigureAwait(false);

    public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        => (await this.DeliverResultAsync(recipient, message, cancellationToken).ConfigureAwait(false)).Outcome;

    internal async Task<EmailProviderResult> DeliverResultAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
    {
        var last = new EmailProviderResult(EmailDeliveryOutcome.Retryable);
        var allRetryable = true;
        for (var index = 0; index < providers.Count; index++)
        {
            var result = await providers[index].DeliverResultAsync(recipient, message, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == EmailDeliveryOutcome.Accepted)
            {
                return result;
            }

            if (result.Outcome != EmailDeliveryOutcome.Retryable)
            {
                allRetryable = false;
            }

            last = result;
            if (index + 1 < providers.Count)
            {
                AuthMetrics.Email("failover");
                logger.ZLogWarning($"Email failover: primary answered {result.Outcome:@Outcome}");
            }
        }

        return allRetryable ? new EmailProviderResult(EmailDeliveryOutcome.Retryable, last.Reset, last.ResetDelay) : last;
    }
}
