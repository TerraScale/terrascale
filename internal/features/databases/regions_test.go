package databases

import (
	"context"
	"errors"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func regionService(t *testing.T) (Service, core.Principal) {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: core.NewID("regions") + "@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	discovered := func(context.Context) ([]core.Region, error) {
		return []core.Region{{Code: "GRU", Name: "São Paulo"}, {Code: "SFO", Name: "San Francisco"}}, nil
	}
	return Service{deps: core.Deps{Store: store, Regions: discovered, Demo: true}}, account.Principal()
}

func TestDatabasePlacementIsGRUSFOOrBothFromDiscovery(t *testing.T) {
	for _, regions := range [][]string{{"GRU"}, {"sfo"}, {"GRU", "SFO"}} {
		service, p := regionService(t)
		db, err := service.Create(t.Context(), p, "orders", regions...)
		if err != nil {
			t.Fatalf("create in %v: %v", regions, err)
		}
		want := make([]string, len(regions))
		for i, region := range regions {
			want[i] = strings.ToUpper(region)
		}
		if !slices.Equal(db.Placement, want) || db.Region != want[0] {
			t.Fatalf("create in %v placed %v (primary %s)", regions, db.Placement, db.Region)
		}
	}

	service, p := regionService(t)
	if _, err := service.Create(t.Context(), p, "orders", "FRA"); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("create in a region discovery does not list = %v, want invalid", err)
	}
	if _, err := service.Create(t.Context(), p, "orders", "GRU", "gru"); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("create with a duplicate region = %v, want invalid", err)
	}
	db, err := service.Create(t.Context(), p, "orders", "GRU")
	if err != nil {
		t.Fatal(err)
	}
	// The home region leads whatever order the regions come in, and stays.
	moved, err := service.SetRegions(t.Context(), p, db.ID, "SFO", "GRU")
	if err != nil || !slices.Equal(moved.Placement, []string{"GRU", "SFO"}) || moved.Region != "GRU" {
		t.Fatalf("set regions = %+v, %v", moved, err)
	}
	if got := replicationDisplay(moved.Placement); got != "Replicated across GRU, SFO · lag not reported" {
		t.Fatalf("replication = %q", got)
	}
	if _, err := service.SetRegions(t.Context(), p, db.ID, "SFO"); !errors.Is(err, errHomeRegion) {
		t.Fatalf("set regions without the home = %v, want refused", err)
	}
	if status, code := failure(errHomeRegion, ErrInvalidRegions); status != 400 || code != ErrHomeRegion {
		t.Fatalf("home region failure = %d %s", status, code)
	}
	if _, err := service.SetRegions(t.Context(), p, db.ID); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("set no regions = %v, want invalid", err)
	}
	if _, err := service.SetRegions(t.Context(), p, "db_missing", "GRU"); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("set regions of a missing database = %v, want not found", err)
	}

	service.deps.Regions = func(context.Context) ([]core.Region, error) { return nil, core.ErrDataPlane }
	if _, err := service.SetRegions(t.Context(), p, db.ID, "GRU"); !errors.Is(err, errRegionsUnavailable) {
		t.Fatalf("set regions without discovery = %v, want unavailable", err)
	}
	if status, code := failure(errRegionsUnavailable, ErrInvalidRegions); status != 503 || code != ErrNoRegions {
		t.Fatalf("failure = %d %s", status, code)
	}
}
