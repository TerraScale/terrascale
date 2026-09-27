package organization

import (
	"slices"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestWizardPlansAreCheapestFirstWithAllowancesAndTrial(t *testing.T) {
	plans := wizardPlans(core.NewStore().Plans(), core.Organization{Kind: kindBusiness})
	keys := make([]string, 0, len(plans))
	for _, plan := range plans {
		keys = append(keys, plan.Key)
	}
	if want := []string{"starter", "pro", "business"}; !slices.Equal(keys, want) {
		t.Fatalf("wizard plans = %q, want %q", keys, want)
	}
	starter := plans[0]
	if want := []string{"5 GB-region storage", "1M operations/month", "5 databases", "5 team members"}; !slices.Equal(starter.Allowances, want) {
		t.Errorf("Starter allowances = %q, want %q", starter.Allowances, want)
	}
	if starter.Trial != "30-day free trial" {
		t.Errorf("Starter trial = %q", starter.Trial)
	}

	used := core.Organization{Kind: kindBusiness, Billing: core.Billing{TrialUsed: true}}
	for _, plan := range wizardPlans(core.NewStore().Plans(), used) {
		if plan.Trial != "" {
			t.Errorf("%s offers a trial to an organization that already had one", plan.Name)
		}
	}
}
