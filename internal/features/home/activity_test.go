package home

import (
	"context"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type seriesMeter struct{ err error }

func (m seriesMeter) Usage(_ context.Context, _ string, scope core.UsageScope) (core.Usage, error) {
	return core.Usage{Series: []core.UsagePoint{{Start: scope.From, Reads: 4, Writes: 1}, {Start: scope.From, Reads: 2}}}, m.err
}

func TestOverviewKeepsMeteredCountsAndPlotsOperations(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "activity@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Usage: seriesMeter{}}}
	summary, err := service.Overview(t.Context(), account.Principal(), "token", "24h")
	if err != nil {
		t.Fatal(err)
	}
	if got := summary.Activity; !got.Metered || got.TotalReads != 6 || got.TotalWrites != 1 || len(got.Reads) != 2 || len(got.Labels) != 2 {
		t.Fatalf("activity = %+v, want 6 reads and 1 write over 2 buckets", got)
	}
	chart := activityChart(summary.Activity)
	if len(chart.Paths) != 1 || chart.AreaPath == "" || chart.Paths[0].Name != "Operations" {
		t.Fatalf("chart = %+v, want one filled operations series", chart)
	}
	if len(chart.Columns) != 2 || chart.Columns[0].Tip.Rows[0].Value != "5" || chart.Columns[1].Tip.Rows[0].Value != "2" {
		t.Fatalf("chart columns = %+v, want measured operation counts 5 and 2", chart.Columns)
	}
	if chart.Grid[0].Label != "5" || chart.Grid[4].Label != "0" {
		t.Fatalf("chart grid = %+v, want a zero-based scale fitted to the measured counts", chart.Grid)
	}
	for _, token := range []string{"", "token"} {
		service.deps.Usage = seriesMeter{err: core.ErrNotMetered}
		summary, err := service.Overview(t.Context(), account.Principal(), token, "")
		if err != nil {
			t.Fatal(err)
		}
		if chart := activityChart(summary.Activity); len(chart.Paths) != 0 {
			t.Fatalf("unmetered activity plotted invented operations: %+v", chart.Paths)
		}
		if got := summary.Activity; got.Metered || got.Sample || len(got.Reads) != 0 || got.Note == "" {
			t.Fatalf("unmetered activity with token %q = %+v", token, got)
		}
	}
}
