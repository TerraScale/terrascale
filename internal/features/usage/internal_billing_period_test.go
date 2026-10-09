package usage

import (
	"errors"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestInternalBillingValidatesMeasuredProjectPeriods(t *testing.T) {
	from := time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC)
	end := from.AddDate(0, 1, 0)
	for _, tc := range []struct {
		name     string
		from, to time.Time
		valid    bool
	}{
		{"month-end", from, end, true},
		{"missing-start", time.Time{}, end, false},
		{"missing-end", from, time.Time{}, false},
		{"wrong-month", from.AddDate(0, -1, 0), end, false},
		{"empty", from, from, false},
		{"before-start", from, from.Add(-time.Second), false},
		{"after-month", from, end.Add(time.Second), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			service, p, meter := internalUsageFixture(t, memstore.New())
			org, err := service.deps.Store.Organization(t.Context(), p.OrganizationID)
			if err != nil {
				t.Fatal(err)
			}
			for id, value := range meter.values {
				value.Period.From, value.Period.To = from, from.Add(24*time.Hour)
				meter.values[id] = value
			}
			_, id := org.Projects[p.ProjectID].TerraBaseIDs(org.ID)
			value := meter.values[id]
			value.Period.From, value.Period.To = tc.from, tc.to
			meter.values[id] = value
			got, err := service.organizationUsage(t.Context(), "user-token", org, from)
			if tc.valid {
				if err != nil || !got.Period.From.Equal(from) || !got.Period.To.Equal(end) {
					t.Fatalf("measured period = %+v, %v", got.Period, err)
				}
			} else if !errors.Is(err, core.ErrUnavailable) {
				t.Fatalf("invalid period accepted: %+v, %v", got.Period, err)
			}
		})
	}
}
