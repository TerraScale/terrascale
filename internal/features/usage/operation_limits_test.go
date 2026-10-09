package usage

import (
	"errors"
	"net/url"
	"reflect"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestOperationFormRejectsInvalidAndDistinguishesAbsentLimits(t *testing.T) {
	for _, value := range []string{"-1", "+1", "1.5", "1e3", "9223372036854775808", "abc"} {
		t.Run(value, func(t *testing.T) {
			if _, valid := operationsFromForm(url.Values{"projRead_p": {value}}, "proj"); valid {
				t.Fatalf("accepted invalid operation limit %q", value)
			}
		})
	}
	for _, values := range []url.Values{{"projRead_": {"1"}}, {"projRead_p": {"1", "2"}}} {
		if _, valid := operationsFromForm(values, "proj"); valid {
			t.Fatalf("accepted malformed fields %v", values)
		}
	}
	changes, valid := operationsFromForm(url.Values{"projRead_p": {"100"}, "projWrite_p": {""}}, "proj")
	if !valid || changes["p"].ReadLimit == nil || *changes["p"].ReadLimit != 100 || changes["p"].WriteLimit == nil || *changes["p"].WriteLimit != 0 || changes["p"].ReplicationLimit != nil {
		t.Fatalf("incorrect partial limits: %+v", changes)
	}
}

func TestSpendingControlsPersistOperationsAndRollBackInvalidScopes(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "limits@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	databaseID := core.NewID()
	err = store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases[databaseID] = core.Database{ID: databaseID, ProjectID: project.ID, Name: "Limits"}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Demo: true}}
	cap := int64(5000)
	paid := true
	change := operationLimitChange{ReadLimit: new(int64(1000)), WriteLimit: new(int64(2000)), ReplicationLimit: new(int64(3000))}
	err = service.SetSpendingControls(t.Context(), p, spendingControls{
		OrganizationCapCents: &cap, PaidExcess: &paid,
		ProjectCaps: map[string]int64{p.ProjectID: 2500}, DatabaseCaps: map[string]int64{databaseID: 1000},
		ProjectOperations: map[string]operationLimitChange{p.ProjectID: change}, DatabaseOperations: map[string]operationLimitChange{databaseID: change},
	})
	if err != nil {
		t.Fatal(err)
	}
	report, err := service.Usage(t.Context(), p, "", "", "")
	want := core.OperationLimits{ReadLimit: 1000, WriteLimit: 2000, ReplicationLimit: 3000}
	if err != nil || report.SpendCapCents != cap || !report.PaidExcessEnabled || report.Projects[0].OperationLimits != want || report.Databases[0].OperationLimits != want || report.Databases[0].EffectiveCapCents != 1000 {
		t.Fatalf("limits were not saved and reported: %+v, %v", report, err)
	}
	// Removing one category must preserve the categories the caller omitted.
	err = service.SetSpendingControls(t.Context(), p, spendingControls{ProjectOperations: map[string]operationLimitChange{p.ProjectID: {ReadLimit: new(int64)}}})
	if err != nil {
		t.Fatal(err)
	}
	before, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	limits := before.Organizations[p.OrganizationID].Projects[p.ProjectID].OperationLimits
	if limits.ReadLimit != 0 || limits.WriteLimit != 2000 || limits.ReplicationLimit != 3000 {
		t.Fatalf("partial change overwrote unrelated limits: %+v", limits)
	}
	for _, controls := range []spendingControls{
		{OrganizationCapCents: new(int64(1)), DatabaseOperations: map[string]operationLimitChange{core.NewID(): change}},
		{ProjectOperations: map[string]operationLimitChange{core.NewID(): change}},
		{DatabaseOperations: map[string]operationLimitChange{databaseID: {WriteLimit: new(int64(-1))}}},
	} {
		if err := service.SetSpendingControls(t.Context(), p, controls); err == nil {
			t.Fatal("invalid limit or scope succeeded")
		}
		after, err := store.SnapshotAs(t.Context(), p)
		if err != nil || !reflect.DeepEqual(before, after) {
			t.Fatalf("invalid change published partial state: %v", err)
		}
	}
	// A member can read limits but cannot change them.
	err = store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.OwnerUserID = core.NewID()
		member := org.Members[p.UserID]
		member.Role = "member"
		org.Members[p.UserID] = member
		a.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.SetSpendingControls(t.Context(), p, spendingControls{ProjectOperations: map[string]operationLimitChange{p.ProjectID: change}}); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("member update = %v, want forbidden", err)
	}
}
