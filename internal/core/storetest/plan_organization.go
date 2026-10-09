package storetest

import (
	"reflect"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestUpsertPlanAndUpdateOrganizationAuthorization(t *testing.T, s core.Store) {
	admin, err := s.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	want(t, err, nil)
	member := account(t, s, "member@example.test")
	stale := admin.Principal()
	want(t, s.Update(ctx, admin.ID, func(a *core.Account) error {
		a.User.PasswordHash = "rotated"
		return nil
	}), nil)
	badScope := member.Principal()
	badScope.OrganizationID = admin.ActiveOrganizationID
	plan := core.Plan{ID: core.NewULID(), Key: "atomic", PriceCents: 100}
	for _, tc := range []struct {
		name string
		p    core.Principal
		want error
	}{
		{"member", member.Principal(), core.ErrForbidden},
		{"stale admin", stale, core.ErrUnauthorized},
		{"invalid scope", badScope, core.ErrForbidden},
		{"missing account", core.Principal{AccountID: core.NewULID()}, core.ErrNotFound},
	} {
		t.Run(tc.name, func(t *testing.T) {
			called := false
			want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, tc.p, member.ActiveOrganizationID, plan, func(org *core.Organization) error {
				called = true
				org.PlanID = plan.ID
				return nil
			}), tc.want)
			if called {
				t.Fatal("unauthorized mutation ran")
			}
			_, err := s.Plan(ctx, plan.ID)
			want(t, err, core.ErrNotFound)
		})
	}
}

func TestUpsertPlanAndUpdateOrganizationRollback(t *testing.T, s core.Store) {
	admin, err := s.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	want(t, err, nil)
	target := account(t, s, "target@example.test")
	p, orgID := admin.Principal(), target.ActiveOrganizationID
	plan := core.Plan{ID: core.NewULID(), Key: "atomic", PriceCents: 100, WriteCentsPerMillion: 10, OperationsPerMonth: 1000}
	want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, orgID, plan, func(org *core.Organization) error {
		org.PlanID = plan.ID
		org.Billing.Internal, org.Billing.PaidExcessEnabled, org.Billing.SpendCapCents = true, true, 1000
		return nil
	}), nil)
	before, err := s.Organization(ctx, orgID)
	want(t, err, nil)
	if before.PlanID != plan.ID || !before.Billing.Internal || !before.Billing.PaidExcessEnabled || before.Billing.SpendCapCents != 1000 {
		t.Fatalf("organization assignment = %+v", before)
	}
	want(t, s.UpdateOrganization(ctx, admin.ActiveOrganizationID, func(org *core.Organization, _ map[string]core.Plan) error {
		org.Billing.CustomerID = "cus_atomic"
		return nil
	}), nil)
	updated := plan
	updated.PriceCents, updated.WriteCentsPerMillion, updated.OperationsPerMonth = 200, 20, 2000
	otherBefore, err := s.Organization(ctx, admin.ActiveOrganizationID)
	want(t, err, nil)
	want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, admin.ActiveOrganizationID, updated, func(org *core.Organization) error {
		org.PlanID, org.Billing.Internal, org.Billing.SpendCapCents = plan.ID, true, 2000
		return nil
	}), core.ErrConflict)
	otherAfter, err := s.Organization(ctx, admin.ActiveOrganizationID)
	want(t, err, nil)
	if !reflect.DeepEqual(otherAfter, otherBefore) {
		t.Fatal("rejected second internal organization changed its policy")
	}
	for _, tc := range []struct {
		name   string
		mutate func(*core.Organization) error
		want   error
	}{
		{"callback failure", func(org *core.Organization) error {
			delete(org.Projects, target.ActiveProjectID)
			org.Billing.SpendCapCents = 2000
			return core.ErrConflict
		}, core.ErrConflict},
		{"organization identity", func(org *core.Organization) error { org.ID = plan.ID; return nil }, core.ErrInvalid},
		{"provider uniqueness", func(org *core.Organization) error { org.Billing.CustomerID = "cus_atomic"; return nil }, core.ErrConflict},
	} {
		t.Run(tc.name, func(t *testing.T) {
			want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, orgID, updated, tc.mutate), tc.want)
			gotPlan, err := s.Plan(ctx, plan.ID)
			want(t, err, nil)
			gotOrg, err := s.Organization(ctx, orgID)
			want(t, err, nil)
			if gotPlan != plan || !reflect.DeepEqual(gotOrg, before) {
				t.Fatal("failed mutation changed the active plan or organization")
			}
		})
	}
	for _, invalid := range []core.Plan{
		{ID: plan.ID, Key: "changed"}, {ID: core.NewULID(), Key: plan.Key},
		{ID: plan.Key, Key: "another"}, {ID: core.NewULID(), Key: plan.ID},
		{ID: core.NewULID(), Key: "invalid", PriceCents: -1},
	} {
		wantErr := core.ErrConflict
		if invalid.PriceCents < 0 {
			wantErr = core.ErrInvalid
		}
		want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, orgID, invalid, func(org *core.Organization) error {
			org.Billing.SpendCapCents = 2000
			return nil
		}), wantErr)
	}
	newPlan := core.Plan{ID: core.NewULID(), Key: "new-atomic"}
	want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, orgID, newPlan, func(*core.Organization) error { return core.ErrConflict }), core.ErrConflict)
	want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, core.NewULID(), newPlan, func(*core.Organization) error { return nil }), core.ErrNotFound)
	want(t, s.UpsertPlanAndUpdateOrganizationAs(ctx, p, orgID, newPlan, nil), core.ErrInvalid)
	_, err = s.Plan(ctx, newPlan.ID)
	want(t, err, core.ErrNotFound)
	gotPlan, err := s.Plan(ctx, plan.ID)
	want(t, err, nil)
	gotOrg, err := s.Organization(ctx, orgID)
	want(t, err, nil)
	if gotPlan != plan || !reflect.DeepEqual(gotOrg, before) {
		t.Fatal("invalid mutation changed the active plan or organization")
	}
}
