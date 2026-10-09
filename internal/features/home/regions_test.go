package home

import (
	"context"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type regionClient struct{ codes []string }

func (c *regionClient) RegionHealth(_ context.Context, codes []string) ([]core.RegionHealth, error) {
	c.codes = codes
	zero := 0.0
	return []core.RegionHealth{{Region: "GRU", Status: "healthy", LatencyMS: &zero}, {Region: "SFO", Status: "lagging"}}, nil
}

func TestOrganizationRegionsCountEachDatabaseOnceAndKeepUnknownLatency(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "regions@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	err = store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.PlanID = "plan_pro"
		project := org.Projects[p.ProjectID]
		project.Databases = map[string]core.Database{"01JC9Q8Y5N7P9R3T5V7X9Z2AC": {Region: "GRU", Regions: []string{"GRU", "SFO", "SFO"}}}
		org.Projects[project.ID] = project
		org.Projects["01JC9QA15N7P9R3T5V7X9Z2AC"] = core.Project{ID: "01JC9QA15N7P9R3T5V7X9Z2AC", OrganizationID: org.ID, Databases: map[string]core.Database{"01JC9Q9Y5N7P9R3T5V7X9Z2AC": {Region: "SFO"}}}
		org.Projects["01JC9QB15N7P9R3T5V7X9Z2AC"] = core.Project{OrganizationID: "other-organization", Databases: map[string]core.Database{"ignored": {Region: "FRA"}}}
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	client := &regionClient{}
	v := loadRegions(t.Context(), core.Deps{Store: store, RegionHealth: client}, p)
	if len(v.Rows) != 2 || v.Rows[0].Code != "GRU" || v.Rows[0].Databases != 1 || v.Rows[0].Latency != "0.00 ms" || v.Rows[1].Databases != 2 || v.Rows[1].Latency != "Not reported" || len(client.codes) != 2 {
		t.Fatalf("regions=%+v; provider codes=%v", v, client.codes)
	}
}
