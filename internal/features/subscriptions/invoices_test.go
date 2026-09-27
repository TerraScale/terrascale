package subscriptions

import (
	"errors"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestPageInvoicesRejectsYearsThatAreNotFourDigits(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	invoices := []core.Invoice{{CreatedAt: now}}
	for _, issued := range []string{"-123", "+123", "0000", "26", "twenty"} {
		if _, err := PageInvoices(invoices, "", issued, 1, now); !errors.Is(err, core.ErrInvalid) {
			t.Errorf("issued %q: err = %v, want an invalid filter", issued, err)
		}
	}
	page, err := PageInvoices(invoices, "", "2026", 1, now)
	if err != nil || page.Total != 1 {
		t.Errorf("issued 2026 = %+v, %v; want the one invoice", page, err)
	}
}
