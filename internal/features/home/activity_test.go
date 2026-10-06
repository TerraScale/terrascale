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

func TestOverviewPlotsMeteredReadsAndWritesApart(t *testing.T) {
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
	for _, token := range []string{"", "token"} {
		service.deps.Usage = seriesMeter{err: core.ErrNotMetered}
		summary, err := service.Overview(t.Context(), account.Principal(), token, "")
		if err != nil {
			t.Fatal(err)
		}
		if got := summary.Activity; got.Metered || got.Sample || len(got.Reads) != 0 || got.Note == "" {
			t.Fatalf("unmetered activity with token %q = %+v", token, got)
		}
	}
}
