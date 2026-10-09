package admin

import (
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestReservedInternalPlanStaysHiddenAfterAdminMutations(t *testing.T) {
	for _, tc := range []struct {
		name, id, key string
		reserved      bool
	}{
		{"reserved key", core.NewULID(), "internal", true},
		{"customer plan", core.NewULID(), "custom", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := memstore.New()
			account, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "plan-admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
			if err != nil {
				t.Fatal(err)
			}
			principal := account.Principal()
			plan := core.Plan{ID: tc.id, Key: tc.key, Name: "Original", PriceCents: 1000}
			if err := store.UpsertPlanAs(t.Context(), principal, plan); err != nil {
				t.Fatal(err)
			}
			service := NewService(store)
			plan.Name, plan.PriceCents, plan.Visible = "Edited", 2000, true
			saved, err := service.SavePlan(t.Context(), principal, plan)
			if err != nil {
				t.Fatal(err)
			}
			if saved.Visible != !tc.reserved || saved.Name != plan.Name || saved.PriceCents != plan.PriceCents {
				t.Fatalf("saved plan = %+v, want edited fields and visible=%v", saved, !tc.reserved)
			}
			persisted, err := store.Plan(t.Context(), plan.ID)
			if err != nil || persisted != saved {
				t.Fatalf("persisted plan = %+v, %v, want %+v", persisted, err, saved)
			}
			if err := service.SetPlanVisibility(t.Context(), principal, plan.ID, false); err != nil {
				t.Fatal(err)
			}
			err = service.SetPlanVisibility(t.Context(), principal, plan.ID, true)
			if tc.reserved && !errors.Is(err, core.ErrInvalid) || !tc.reserved && err != nil {
				t.Fatalf("publish plan = %v, reserved=%v", err, tc.reserved)
			}
			persisted, err = store.Plan(t.Context(), plan.ID)
			if err != nil || persisted.Visible != !tc.reserved {
				t.Fatalf("plan after visibility toggle = %+v, %v, want visible=%v", persisted, err, !tc.reserved)
			}
		})
	}
}
