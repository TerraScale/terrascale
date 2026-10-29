using System.Globalization;
using TerraScale.Dashboard.Common.Formatting;

namespace TerraScale.Dashboard.Features.Billing.Invoices
{
    public sealed record BillingInvoice(string Id, string Number, string Status, long AmountCents, string Currency,
        DateTimeOffset CreatedAt, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd,
        string HostedUrl = "", string PdfUrl = "")
    {
        public long? RemainingCents { get; init; }
    }
    public sealed record BillingInvoiceList(IReadOnlyList<BillingInvoice> Invoices, int Page, int PageCount, int Total);
    public sealed record InvoiceHistoryPage(BillingInvoiceList Listing, string Status, string Issued)
    {
        public int First => Listing.Total == 0 ? 0 : (Listing.Page - 1) * InvoiceHistory.PageSize + 1;
        public string Href(string project, int page)
        {
            var values = new List<KeyValuePair<string, string?>> { new("tab", "billing") };
            if (project.Length > 0)
            {
                values.Add(new("project", project));
            }

            if (Status.Length > 0)
            {
                values.Add(new("status", Status));
            }

            if (Issued.Length > 0)
            {
                values.Add(new("issued", Issued));
            }

            if (page > 1)
            {
                values.Add(new("page", page.ToString(CultureInfo.InvariantCulture)));
            }

            return "/organization" + QueryString.Create(values.OrderBy(value => value.Key, StringComparer.Ordinal));
        }
    }
    // Catalog base subscription fee at renewal; usage and invoice adjustments are not included.
    public sealed record InvoiceRenewal(DateTimeOffset At, long AmountCents, string Currency, string Interval, bool Ends = false, DateTimeOffset? PeriodStart = null);
    public sealed record InvoiceSummary(string NextInvoice, string NextDetail, string Period, string PeriodDetail,
        string Outstanding, string OutstandingDetail, bool HasPeriod = false, int PeriodPercent = 0);

    public static class InvoiceHistory
    {
        public const int PageSize = 10;
        public static IReadOnlyList<(string Value, string Label)> StatusOptions { get; } = [("paid", "Paid"), ("open", "Open"), ("void", "Void")];
        public static IReadOnlyList<(string Value, string Label)> IssuedOptions(DateTimeOffset now)
        {
            return [("3m", "Last 3 months"), ("6m", "Last 6 months"), ("12m", "Last 12 months"),
             (now.Year.ToString(CultureInfo.InvariantCulture), now.Year.ToString(CultureInfo.InvariantCulture)),
             ((now.Year - 1).ToString(CultureInfo.InvariantCulture), (now.Year - 1).ToString(CultureInfo.InvariantCulture))];
        }

        public static BillingInvoiceList Page(IReadOnlyList<BillingInvoice> invoices, string status, string issued, int page, DateTimeOffset now)
        {
            if (status.Length > 0 && !StatusOptions.Any(option => option.Value == status))
            {
                throw new AccountAccessException("status must be paid, open, or void", 422);
            }

            DateTimeOffset? cutoff = null;
            var year = 0;
            if (issued is "3m" or "6m" or "12m")
            {
                var months = int.Parse(issued[..^1], CultureInfo.InvariantCulture);
                // Match Go AddDate: a day beyond the target month rolls into the next month.
                var first = new DateTimeOffset(now.Year, now.Month, 1, now.Hour, now.Minute, now.Second, now.Offset);
                cutoff = first.AddMonths(-months).AddDays(now.Day - 1).AddTicks(now.Ticks % TimeSpan.TicksPerSecond);
            }
            else if (issued.Length > 0 && (issued.Length != 4 || !issued.All(char.IsAsciiDigit) ||
                !int.TryParse(issued, NumberStyles.None, CultureInfo.InvariantCulture, out year) || year < 1))
            {
                throw new AccountAccessException("issued must be 3m, 6m, 12m, or a four-digit year", 422);
            }

            var rows = invoices.Where(invoice => (status.Length == 0 || invoice.Status == status) &&
                (year == 0 || invoice.CreatedAt.UtcDateTime.Year == year) && (cutoff is null || invoice.CreatedAt >= cutoff))
                .OrderByDescending(invoice => invoice.CreatedAt).ToArray();
            var pages = Math.Max(1, (rows.Length + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 1, pages);
            return new([.. rows.Skip((page - 1) * PageSize).Take(PageSize)], page, pages, rows.Length);
        }

        public static InvoiceHistoryPage ForBrowser(IReadOnlyList<BillingInvoice> invoices, IQueryCollection query, DateTimeOffset now)
        {
            var status = query["status"].ToString();
            if (!StatusOptions.Any(option => option.Value == status))
            {
                status = "";
            }

            var issued = query["issued"].ToString();
            if (!IssuedOptions(now).Any(option => option.Value == issued))
            {
                issued = "";
            }

            var page = int.TryParse(query["page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 1;
            return new(Page(invoices, status, issued, page, now), status, issued);
        }

        public static string SafeUrl(string? raw)
        {
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
        }

        public static InvoiceSummary Summarize(IReadOnlyList<BillingInvoice> invoices, DateTimeOffset now, InvoiceRenewal? renewal = null)
        {
            var summary = new InvoiceSummary("None scheduled", "No upcoming invoice is scheduled.", "Unavailable",
                "No current billing period is recorded.", "Not recorded", "No invoices are recorded.");
            if (renewal is { } next && next.At > now)
            {
                summary = summary with
            {
                NextInvoice = next.Ends ? "None scheduled" : MoneyFormat.Money(next.AmountCents, next.Currency) + " base fee",
                NextDetail = next.Ends ? "The subscription ends on " + next.At.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                    : "Base subscription at renewal on " + next.At.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + ". Usage, credits, discounts and taxes may change the final invoice."
            };
            }

            var current = invoices.Where(invoice => invoice.PeriodStart is { } start && invoice.PeriodEnd is { } end &&
                start <= now && now < end).OrderByDescending(invoice => invoice.PeriodStart).FirstOrDefault();
            (DateTimeOffset Start, DateTimeOffset End)? period = current is null ? null : (current.PeriodStart!.Value, current.PeriodEnd!.Value);
            var periodSource = current is null ? "the subscription renewal date" : "a recorded invoice period";
            if (renewal is { PeriodStart: { } starts, At: var ends } && starts > DateTimeOffset.UnixEpoch && starts <= now && now < ends)
            {
                period = (starts, ends);
                periodSource = "the current subscription period";
            }
            if (period is null && renewal is { At: var renews } && renews > now && renewal.Interval is "annual" or "monthly")
            {
                var started = renewal.Interval == "annual" ? renews.AddYears(-1) : renews.AddMonths(-1);
                if (started <= now)
                {
                    period = (started, renews);
                }
            }
            if (period is { } known)
            {
                var (start, end) = known;
                var days = Math.Max(1, (int)(end - start).TotalDays);
                var elapsed = Math.Clamp((int)(now - start).TotalDays, 0, days);
                summary = summary with
                {
                    Period = start.UtcDateTime.ToString(start.UtcDateTime.Year == end.UtcDateTime.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture) + " – " + end.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                    PeriodDetail = $"Day {elapsed} of {days} · based on " + periodSource,
                    HasPeriod = true,
                    PeriodPercent = Math.Clamp(Math.Max(elapsed > 0 ? 1 : 0, (int)Math.Round(elapsed * 100d / days, MidpointRounding.AwayFromZero)), 0, 100)
                };
            }
            var open = invoices.Where(invoice => invoice.Status.Equals("open", StringComparison.OrdinalIgnoreCase)).ToArray();
            var upcoming = open.Where(invoice => invoice.CreatedAt >= now).OrderBy(invoice => invoice.CreatedAt).FirstOrDefault();
            if (upcoming is not null)
            {
                summary = summary with
            {
                NextInvoice = MoneyFormat.Money(upcoming.AmountCents, upcoming.Currency),
                NextDetail = "Recorded open invoice dated " + upcoming.CreatedAt.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            };
            }

            if (invoices.Count > 0)
            {
                var currencies = open.Select(invoice => invoice.Currency.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
                long amount = 0;
                foreach (var invoice in open)
                {
                    amount = checked(amount + Math.Max(0, invoice.RemainingCents ?? invoice.AmountCents));
                }

                summary = summary with
                {
                    OutstandingDetail = ValueFormat.Plural(open.Length, "open invoice") + " in recorded history",
                    Outstanding = open.Length == 0 ? "0 open invoices" : currencies.Length > 1 ? "Multiple currencies" : MoneyFormat.Money(amount, currencies[0])
                };
            }
            return summary;
        }
    }
}
