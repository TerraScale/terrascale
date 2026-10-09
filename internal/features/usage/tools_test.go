package usage

import (
	"reflect"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestSpendingToolIgnoresOmittedLimitsAndAuditsExplicitZero(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "tool-limits@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	databaseID := core.NewID()
	if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.OperationLimits.ReadLimit = 100
		project.Databases[databaseID] = core.Database{ID: databaseID, ProjectID: project.ID, Name: "Limits", OperationLimits: core.OperationLimits{WriteLimit: 200}}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	tools := core.NewTools(store)
	mountTools(tools, Service{deps: core.Deps{Store: store, Demo: true}})
	var route core.Route
	for _, candidate := range tools.Routes() {
		if candidate.Spec.Name == "set_spending_controls" {
			route = candidate
		}
	}
	if route.Call == nil {
		t.Fatal("spending controls route is missing")
	}
	before, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	for _, args := range []map[string]any{
		{},
		{"projectCaps": []map[string]any{{"id": p.ProjectID}}},
		{"databaseCaps": []map[string]any{{"id": databaseID}}},
	} {
		if _, err := route.Call(t.Context(), p, args); err != nil {
			t.Fatal(err)
		}
		after, err := store.SnapshotAs(t.Context(), p)
		if err != nil || !reflect.DeepEqual(before, after) {
			t.Fatalf("omitted limits changed state or added an audit event: %v", err)
		}
	}
	if _, err := route.Call(t.Context(), p, map[string]any{
		"projectCaps":  []map[string]any{{"id": p.ProjectID, "readLimit": 0}},
		"databaseCaps": []map[string]any{{"id": databaseID, "writeLimit": 0}},
	}); err != nil {
		t.Fatal(err)
	}
	after, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	project := after.Organizations[p.OrganizationID].Projects[p.ProjectID]
	if project.OperationLimits.ReadLimit != 0 || project.Databases[databaseID].OperationLimits.WriteLimit != 0 || len(after.AuditEvents) != len(before.AuditEvents)+1 || after.AuditEvents[len(after.AuditEvents)-1].Action != "usage.spending_controls_updated" {
		t.Fatalf("explicit zero limits were not cleared and audited: %+v", after)
	}
}
