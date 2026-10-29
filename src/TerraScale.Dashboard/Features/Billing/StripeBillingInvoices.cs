using Stripe;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;
using TerraScale.Dashboard.Features.Billing.Invoices;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed partial class StripeBilling
    {
        public async Task<IReadOnlyList<BillingInvoice>> InvoicesAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await MemberAsync(context, cancellationToken);
            var binding = await ReadAsync(context.Organization!.Id, cancellationToken);
            if (binding is null || binding.CustomerId.Length == 0)
            {
                return [];
            }

            var customer = await state.GetAsync(CustomersCollection, binding.CustomerId, cancellationToken);
            if (customer is null || AccountService.Read<CustomerBinding>(customer).OrganizationId != context.Organization.Id)
            {
                throw new IOException("Billing customer ownership changed.");
            }

            var client = await ClientAsync(cancellationToken);
            var invoices = new List<BillingInvoice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var options = new InvoiceListOptions { Customer = binding.CustomerId, Limit = 100 };
            while (true)
            {
                var page = await client.V1.Invoices.ListAsync(options, cancellationToken: cancellationToken);
                foreach (var invoice in page.Data)
                {
                    if (string.IsNullOrEmpty(invoice.Id) || invoice.CustomerId != binding.CustomerId || !ids.Add(invoice.Id))
                    {
                        throw new IOException("Stripe invoice identity or pagination changed.");
                    }
                    // Subscription invoices carry the service period on their lines; the invoice's own
                    // period_start and period_end are the same instant for creation and proration invoices.
                    var lines = invoice.Lines?.Data?.Where(line => line.Period is { } period && period.End > period.Start).ToArray() ?? [];
                    var periodStart = lines.Length > 0 ? lines.Min(line => line.Period.Start) : invoice.PeriodStart;
                    var periodEnd = lines.Length > 0 ? lines.Max(line => line.Period.End) : invoice.PeriodEnd;
                    invoices.Add(new(invoice.Id, invoice.Number ?? "", invoice.Status ?? "", invoice.AmountDue, invoice.Currency ?? "",
                        new(invoice.Created, TimeSpan.Zero), Timestamp(periodStart), Timestamp(periodEnd),
                        InvoiceHistory.SafeUrl(invoice.HostedInvoiceUrl), InvoiceHistory.SafeUrl(invoice.InvoicePdf))
                    { RemainingCents = invoice.AmountRemaining });
                }
                if (!page.HasMore)
                {
                    break;
                }

                if (page.Data.Count == 0)
                {
                    throw new IOException("Stripe invoice pagination did not advance.");
                }

                options.StartingAfter = page.Data[^1].Id;
            }
            return invoices;
        }

        private static DateTimeOffset? Timestamp(DateTime value)
        {
            return value <= DateTime.UnixEpoch ? null : new(value, TimeSpan.Zero);
        }
    }
}
