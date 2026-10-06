package storetest

import (
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func account(t *testing.T, s core.Store, email string) core.Account {
	t.Helper()
	a, err := s.CreateAccount(ctx, core.User{Name: "Owner", Email: email, PasswordHash: "hash"})
	want(t, err, nil)
	return a
}

func want(t *testing.T, got, expected error) {
	t.Helper()
	if !errors.Is(got, expected) {
		t.Fatalf("error = %v, want %v", got, expected)
	}
}

func snapshot(t *testing.T, s core.Store, p core.Principal) core.Account {
	t.Helper()
	a, err := s.SnapshotAs(ctx, p)
	want(t, err, nil)
	return a
}

// Start the competing operations together; every goroutine reports its result
// to the test goroutine rather than calling Fatal from a worker.
func concurrent(t *testing.T, operations ...func() error) []error {
	t.Helper()
	start := make(chan struct{})
	errs := make([]error, len(operations))
	var wg sync.WaitGroup
	for i, operation := range operations {
		wg.Go(func() { <-start; errs[i] = operation() })
	}
	close(start)
	wg.Wait()
	return errs
}

func TestRegistrationIdentity(t *testing.T, s core.Store) {
	a := account(t, s, "  OWNER@example.test  ")
	if a.User.Email != "owner@example.test" || len(a.Organizations) != 1 {
		t.Fatalf("registration = %+v", a)
	}
	_, err := s.CreateAccount(ctx, core.User{Name: "Duplicate", Email: "Owner@Example.Test", PasswordHash: "hash"})
	want(t, err, core.ErrConflict)
	got, err := s.FindByEmail(ctx, " OWNER@EXAMPLE.TEST ")
	want(t, err, nil)
	if got.ID != a.ID {
		t.Fatal("normalized email resolved another account")
	}

	// Both account identity and organization identity are create-only.
	duplicate := core.CloneAccount(a)
	duplicate.User.Email = "other@example.test"
	want(t, s.InsertAccount(ctx, duplicate), core.ErrConflict)
	duplicate.ID, duplicate.User.ID = core.NewID("acct"), core.NewID("usr")
	want(t, s.InsertAccount(ctx, duplicate), core.ErrConflict)
	_, err = s.FindByEmail(ctx, "other@example.test")
	want(t, err, core.ErrNotFound)
}

func TestConcurrentEmailRegistration(t *testing.T, s core.Store) {
	errs := concurrent(t,
		func() error {
			_, err := s.CreateAccount(ctx, core.User{Name: "One", Email: "race@example.test", PasswordHash: "hash"})
			return err
		},
		func() error {
			_, err := s.CreateAccount(ctx, core.User{Name: "Two", Email: "RACE@example.test", PasswordHash: "hash"})
			return err
		},
	)
	wins, conflicts := 0, 0
	for _, err := range errs {
		if err == nil {
			wins++
		} else if errors.Is(err, core.ErrConflict) {
			conflicts++
		} else {
			t.Fatal(err)
		}
	}
	if wins != 1 || conflicts != 1 {
		t.Fatalf("registration results = %v", errs)
	}
	admin, err := s.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	want(t, err, nil)
	accounts, err := s.PlatformAccounts(ctx, admin.Principal())
	want(t, err, nil)
	if len(accounts) != 2 {
		t.Fatalf("rejected registration left an account: %d", len(accounts))
	}
}

func TestDetachedValuesAndAtomicRollback(t *testing.T, s core.Store) {
	a := account(t, s, "copy@example.test")
	p := a.Principal()
	a.User.Name = "outside"
	a.Organizations[p.OrganizationID].Members[p.UserID] = core.Member{Name: "outside"}
	copy := snapshot(t, s, p)
	copy.User.Name = "changed snapshot"
	delete(copy.Organizations[p.OrganizationID].Projects, p.ProjectID)
	want(t, s.UpdateAs(ctx, p, func(a *core.Account) error {
		a.User.Name = "rolled back"
		org := a.Organizations[p.OrganizationID]
		org.Billing.CustomerID = "cus_rolled_back"
		a.Organizations[org.ID] = org
		return core.ErrInvalid
	}), core.ErrInvalid)
	stored := snapshot(t, s, p)
	if stored.User.Name != "Owner" || stored.Organizations[p.OrganizationID].Billing.CustomerID != "" || len(stored.Organizations[p.OrganizationID].Projects) != 1 {
		t.Fatalf("copy or rejected callback mutated state: %+v", stored)
	}
	org, err := s.Organization(ctx, p.OrganizationID)
	want(t, err, nil)
	delete(org.Members, p.UserID)
	if len(snapshot(t, s, p).Organizations[p.OrganizationID].Members) != 1 {
		t.Fatal("organization leaked its maps")
	}
}

func TestCredentialGuardRails(t *testing.T, s core.Store) {
	a := account(t, s, "credentials@example.test")
	p := a.Principal()
	for _, mutate := range []func(*core.Account){
		func(a *core.Account) { a.ID = "changed" },
		func(a *core.Account) { a.User.ID = "changed" },
		func(a *core.Account) { a.User.Email = "changed@example.test" },
		func(a *core.Account) { a.User.PasswordHash = "" },
	} {
		want(t, s.Update(ctx, a.ID, func(a *core.Account) error { mutate(a); return nil }), core.ErrInvalid)
	}
	want(t, s.Update(ctx, a.ID, func(a *core.Account) error { a.AuthVersion = 999; return nil }), nil)
	if got := snapshot(t, s, p).AuthVersion; got != p.AuthVersion {
		t.Fatalf("forged auth version = %d", got)
	}
	_, secret, err := s.CreateAccessToken(ctx, p, "before rotation", time.Hour)
	want(t, err, nil)
	want(t, s.ChangeEmailAs(ctx, p, "changed@example.test", "wrong hash"), core.ErrConflict)
	want(t, s.Update(ctx, a.ID, func(a *core.Account) error { a.User.PasswordHash = "rotated"; return nil }), nil)
	_, err = s.SessionPrincipal(ctx, a.ID, a.User.ID, p.AuthVersion)
	want(t, err, core.ErrUnauthorized)
	_, err = s.AccessTokenPrincipal(ctx, secret)
	want(t, err, core.ErrUnauthorized)
	current, err := s.CurrentPrincipal(ctx, a.ID, a.User.ID)
	want(t, err, nil)
	if current.AuthVersion != p.AuthVersion+1 || len(snapshot(t, s, current).AccessTokens) != 0 {
		t.Fatal("rotation did not revoke credentials")
	}
}

func TestAccessTokenCapAndExpiry(t *testing.T, s core.Store) {
	a := account(t, s, "tokens@example.test")
	p := a.Principal()
	tokens := make([]core.AccessToken, core.MaxAccessTokens)
	for i := range tokens {
		tokens[i], _, _ = core.NewAccessToken(fmt.Sprint(i), time.Hour, time.Now())
	}
	want(t, s.UpdateAs(ctx, p, func(a *core.Account) error { a.AccessTokens = tokens; return nil }), nil)
	_, _, err := s.CreateAccessToken(ctx, p, "over cap", time.Hour)
	want(t, err, core.ErrConflict)
	want(t, s.UpdateAs(ctx, p, func(a *core.Account) error { a.AccessTokens[0].ExpiresAt = time.Now().Add(-time.Hour); return nil }), nil)
	token, secret, err := s.CreateAccessToken(ctx, p, "replacement", time.Hour)
	want(t, err, nil)
	if len(snapshot(t, s, p).AccessTokens) != core.MaxAccessTokens {
		t.Fatal("expired token still occupied the cap")
	}
	resolved, err := s.AccessTokenPrincipal(ctx, secret)
	want(t, err, nil)
	if resolved != p {
		t.Fatalf("token principal = %+v, want %+v", resolved, p)
	}
	want(t, s.RevokeAccessToken(ctx, p, token.ID), nil)
	_, err = s.AccessTokenPrincipal(ctx, secret)
	want(t, err, core.ErrUnauthorized)
	want(t, s.RevokeAccessToken(ctx, p, token.ID), core.ErrNotFound)
}

func TestConcurrentOrganizationDatabaseLimit(t *testing.T, s core.Store) {
	a := account(t, s, "database-race@example.test")
	p := a.Principal()
	extraID := core.NewID("prj")
	want(t, s.UpdateAs(ctx, p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.Projects[extraID] = core.Project{ID: extraID, OrganizationID: org.ID, Name: "Extra", Status: "active", Databases: make(map[string]core.Database)}
		a.Organizations[org.ID] = org
		return nil
	}), nil)
	extra := p
	extra.ProjectID = extraID
	errs := concurrent(t, func() error { return addDatabase(s, p) }, func() error { return addDatabase(s, extra) })
	wins, rejected := 0, 0
	for _, err := range errs {
		if err == nil {
			wins++
		} else if errors.Is(err, core.ErrDatabaseLimit) {
			rejected++
		} else {
			t.Fatal(err)
		}
	}
	if wins != 1 || rejected != 1 || databaseCount(t, s, p) != 1 {
		t.Fatalf("per-organization limit results = %v", errs)
	}
}

func TestConcurrentAggregateUpdates(t *testing.T, s core.Store) {
	a := account(t, s, "updates@example.test")
	p := a.Principal()
	update := func() error {
		return s.UpdateAs(ctx, p, func(a *core.Account) error {
			if a.OnboardingStep == "toggled" {
				a.OnboardingStep = "payment-method"
			} else {
				a.OnboardingStep = "toggled"
			}
			return nil
		})
	}
	for _, err := range concurrent(t, update, update) {
		want(t, err, nil)
	}
	if snapshot(t, s, p).OnboardingStep != "payment-method" {
		t.Fatal("one concurrent aggregate update was lost")
	}
}

func TestProviderIdentityUniqueness(t *testing.T, s core.Store) {
	one := account(t, s, "one@example.test").Principal()
	two := account(t, s, "two@example.test").Principal()
	for _, kind := range []string{"customer", "subscription"} {
		bind := func(p core.Principal) error {
			return s.UpdateOrganization(ctx, p.OrganizationID, func(org *core.Organization, _ map[string]core.Plan) error {
				if kind == "customer" {
					org.Billing.CustomerID = "cus_shared"
				} else {
					org.Billing.SubscriptionID = "sub_shared"
				}
				return nil
			})
		}
		errs := concurrent(t, func() error { return bind(one) }, func() error { return bind(two) })
		wins, conflicts := 0, 0
		for _, err := range errs {
			if err == nil {
				wins++
			} else if errors.Is(err, core.ErrConflict) {
				conflicts++
			} else {
				t.Fatal(err)
			}
		}
		if wins != 1 || conflicts != 1 {
			t.Fatalf("%s uniqueness = %v", kind, errs)
		}
	}
}

func TestPlanCatalogAndExternalReferences(t *testing.T, s core.Store) {
	admin, err := s.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	want(t, err, nil)
	p := admin.Principal()
	plan := core.Plan{ID: "plan_custom", Key: "custom", Name: "Custom", MaxDatabases: 2}
	want(t, s.UpsertPlanAs(ctx, p, plan), nil)
	for _, conflict := range []core.Plan{
		{ID: plan.ID, Key: "changed"}, {ID: "other", Key: plan.Key},
		{ID: plan.Key, Key: "another"}, {ID: "another", Key: plan.ID},
	} {
		want(t, s.UpsertPlanAs(ctx, p, conflict), core.ErrConflict)
	}
	other := account(t, s, "other@example.test").Principal()
	want(t, setPlan(s, other, plan.ID), nil)
	want(t, s.DeletePlanAs(ctx, p, plan.ID), core.ErrConflict)
	want(t, setPlan(s, other, "plan_free"), nil)
	checkout := core.CheckoutSession{ID: "checkout_provider", PlanID: plan.ID, Status: "open", ExpiresAt: time.Now().Add(-time.Hour)}
	want(t, s.UpdateOrganization(ctx, other.OrganizationID, func(org *core.Organization, _ map[string]core.Plan) error {
		org.CheckoutSessions[checkout.ID] = checkout
		return nil
	}), nil)
	want(t, s.DeletePlanAs(ctx, p, plan.ID), core.ErrConflict) // Provider expiry does not make an open checkout terminal.
	want(t, s.UpdateOrganization(ctx, other.OrganizationID, func(org *core.Organization, _ map[string]core.Plan) error {
		c := org.CheckoutSessions[checkout.ID]
		c.Status = "complete"
		org.CheckoutSessions[c.ID] = c
		return nil
	}), nil)
	want(t, s.DeletePlanAs(ctx, p, plan.ID), nil)
	_, err = s.Plan(ctx, plan.Key)
	want(t, err, core.ErrNotFound)
	want(t, s.RecordPlatformAdminAction(ctx, p, "plan.delete", plan.ID, "unused"), nil)
	actions, err := s.PlatformAdminActions(ctx, p)
	want(t, err, nil)
	if len(actions) != 1 || actions[0].Target != plan.ID {
		t.Fatalf("admin log = %+v", actions)
	}
}

func TestScopeGuardAndMembershipRemoval(t *testing.T, s core.Store) {
	member, owner := joinedMember(t, s)
	selected := member.Principal()
	var personalID string
	for id := range member.Organizations {
		if id != owner.OrganizationID {
			personalID = id
		}
	}
	want(t, s.UpdateAs(ctx, selected, func(a *core.Account) error {
		org := a.Organizations[personalID]
		org.Name = "out of scope"
		a.Organizations[org.ID] = org
		return nil
	}), core.ErrForbidden)
	want(t, s.UpdateAs(ctx, owner, func(a *core.Account) error {
		org := a.Organizations[owner.OrganizationID]
		delete(org.Members, member.User.ID)
		a.Organizations[org.ID] = org
		return nil
	}), nil)
	p, err := s.SessionPrincipal(ctx, member.ID, member.User.ID, member.AuthVersion)
	want(t, err, nil)
	if p.OrganizationID != personalID || p.ProjectID == "" {
		t.Fatalf("removed member's fallback = %+v", p)
	}
	_, err = s.SnapshotAs(ctx, selected)
	want(t, err, core.ErrForbidden)
}

func TestSetPlatformAdmin(t *testing.T, s core.Store) {
	target := account(t, s, "ops@example.test")
	reader, err := s.CreateAccount(ctx, core.User{Name: "Reader", Email: "reader@example.test", PasswordHash: "hash", PlatformAdmin: true})
	want(t, err, nil)
	for i, step := range []struct{ admin, changed bool }{{true, true}, {true, false}, {false, true}, {false, false}} {
		changed, err := s.SetPlatformAdmin(ctx, target.ID, step.admin)
		want(t, err, nil)
		got, err := s.FindByEmail(ctx, "ops@example.test")
		want(t, err, nil)
		if changed != step.changed || got.User.PlatformAdmin != step.admin {
			t.Fatalf("step %d: changed = %v, admin = %v, want %+v", i, changed, got.User.PlatformAdmin, step)
		}
	}
	actions, err := s.PlatformAdminActions(ctx, reader.Principal())
	want(t, err, nil)
	if len(actions) != 2 || actions[0].Action != "platformAdmin.grant" || actions[1].Action != "platformAdmin.revoke" ||
		actions[0].ActorUserID != target.User.ID || actions[0].Target != "ops@example.test" {
		t.Fatalf("admin log = %+v", actions)
	}
	_, err = s.SetPlatformAdmin(ctx, "missing", true)
	want(t, err, core.ErrNotFound)
}
