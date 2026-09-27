package subscriptions

import (
	"net/url"
	"slices"
	"strconv"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// InvoicePageSize is how many invoices one billing page lists.
const InvoicePageSize = 10

// InvoiceStatuses are the invoice statuses billing filters accept.
var InvoiceStatuses = []string{"paid", "open", "void"}

// InvoicePage is one page of an organization's invoices, newest first.
type InvoicePage struct {
	Invoices  []core.Invoice
	Page      int
	PageCount int
	Total     int
}

// PageInvoices filters invoices like the billing tab and returns the 1-based
// page, clamped to the pages that exist. An empty status or issued matches
// every invoice; issued is 3m, 6m, 12m, or a four-digit year.
func PageInvoices(invoices []core.Invoice, status, issued string, page int, now time.Time) (InvoicePage, error) {
	if status != "" && !slices.Contains(InvoiceStatuses, status) {
		return InvoicePage{}, core.Invalid("status must be paid, open, or void")
	}
	var cutoff time.Time
	year := 0
	switch issued {
	case "":
	case "3m":
		cutoff = now.AddDate(0, -3, 0)
	case "6m":
		cutoff = now.AddDate(0, -6, 0)
	case "12m":
		cutoff = now.AddDate(0, -12, 0)
	default:
		parsed, err := strconv.Atoi(issued)
		if err != nil || len(issued) != 4 || issued[0] == '+' || issued[0] == '-' || parsed < 1 {
			return InvoicePage{}, core.Invalid("issued must be 3m, 6m, 12m, or a four-digit year")
		}
		year = parsed
	}
	filtered := slices.DeleteFunc(slices.Clone(invoices), func(invoice core.Invoice) bool {
		return status != "" && invoice.Status != status ||
			year != 0 && invoice.CreatedAt.UTC().Year() != year ||
			invoice.CreatedAt.Before(cutoff)
	})
	slices.SortStableFunc(filtered, func(a, b core.Invoice) int { return b.CreatedAt.Compare(a.CreatedAt) })
	result := InvoicePage{Total: len(filtered), PageCount: max(1, (len(filtered)+InvoicePageSize-1)/InvoicePageSize)}
	result.Page = min(max(page, 1), result.PageCount)
	start := (result.Page - 1) * InvoicePageSize
	result.Invoices = filtered[start:min(start+InvoicePageSize, len(filtered))]
	return result, nil
}

// InvoiceURL keeps only well-formed HTTPS invoice links from the provider.
func InvoiceURL(raw string) string {
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme != "https" || parsed.Hostname() == "" || parsed.User != nil {
		return ""
	}
	return parsed.String()
}
