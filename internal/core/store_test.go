package core

import (
	"errors"
	"sync"
	"testing"
	"time"
)

func testAccount(t *testing.T, store *Store, email string) Account {
	t.Helper()
	a, err := store.CreateAccount(User{Name: email, Email: email, PasswordHash: "hashed"})
	if err != nil {
		t.Fatal(err)
	}
	return a
}

func TestCreateAccountIsolatedAndAtomic(t *testing.T) {
	store := NewStore()
	a := testAccount(t, store, "a@example.com")
	b := testAccount(t, store, "b@example.com")
	if a.ActiveOrganizationID == b.ActiveOrganizationID || a.ActiveProjectID == b.ActiveProjectID {
		t.Fatal("accounts share initial resources")
	}
	if _, err := store.CreateAccount(User{Name: "Again", Email: " A@EXAMPLE.COM ", PasswordHash: "hashed"}); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate email: %v", err)
	}
	p := a.Principal()
	if err := store.UpdateAs(p, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		if project.SharedResultConfigs == nil {
			t.Fatal("default project has no shared-result configuration map")
		}
		project.SharedResultConfigs["shared"] = SharedResultConfig{ID: "shared", DatabaseID: "db", Name: "Summary", Query: "SELECT 1", Transport: "http", Status: "saved"}
		project.Databases = map[string]Database{"db": {
			ID: "db", ProjectID: project.ID,
			Documents: map[string]map[string][]byte{"coll": {"doc": []byte(`{"ok":true}`)}},
			Backups:   []Backup{{ID: "backup", Documents: map[string]map[string][]byte{"coll": {"doc": []byte(`{"ok":true}`)}}}},
		}}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	snapshot, err := store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	db := snapshot.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases["db"]
	db.Documents["coll"]["doc"][0] = 'x'
	db.Backups[0].Documents["coll"]["doc"][0] = 'x'
	delete(snapshot.Organizations[p.OrganizationID].Projects[p.ProjectID].SharedResultConfigs, "shared")
	unchanged, _ := store.SnapshotAs(p)
	storedProject := unchanged.Organizations[p.OrganizationID].Projects[p.ProjectID]
	stored := storedProject.Databases["db"]
	if stored.Documents["coll"]["doc"][0] == 'x' || stored.Backups[0].Documents["coll"]["doc"][0] == 'x' || storedProject.SharedResultConfigs["shared"].ID != "shared" {
		t.Fatal("snapshot aliases nested state")
	}
	abort := errors.New("abort")
	if err := store.UpdateAs(p, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		delete(project.SharedResultConfigs, "shared")
		db := project.Databases["db"]
		db.Documents["coll"]["doc"][0] = 'x'
		project.Databases["db"] = db
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return abort
	}); !errors.Is(err, abort) {
		t.Fatalf("failed mutation error: %v", err)
	}
	unchanged, _ = store.SnapshotAs(p)
	unchangedProject := unchanged.Organizations[p.OrganizationID].Projects[p.ProjectID]
	if unchangedProject.Databases["db"].Documents["coll"]["doc"][0] == 'x' || unchangedProject.SharedResultConfigs["shared"].ID != "shared" {
		t.Fatal("failed mutation leaked state")
	}
}

func TestSharedMembershipRevocationIsImmediate(t *testing.T) {
	store := NewStore()
	owner := testAccount(t, store, "owner@example.com")
	member := testAccount(t, store, "member@example.com")
	op := owner.Principal()
	if err := store.UpdateAs(op, func(a *Account) error {
		org := a.Organizations[op.OrganizationID]
		org.Invitations["invite"] = Invitation{
			ID: "invite", Email: member.User.Email, Role: "viewer", Status: "pending",
			TokenHash: "token-hash", ExpiresAt: time.Now().Add(time.Hour),
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	publicOrg, publicInvitation, found := store.FindInvitation("token-hash")
	if !found || publicOrg.Name == "" || len(publicOrg.Projects) != 0 || publicInvitation.TokenHash != "" {
		t.Fatal("public invitation lookup exposed organization state or token hash")
	}
	if _, err := store.AcceptInvitation(member.Principal(), "token-hash"); err != nil {
		t.Fatal(err)
	}
	joined, _ := store.Snapshot(member.ID)
	mp := joined.Principal()
	if mp.OrganizationID != op.OrganizationID {
		t.Fatal("invite did not select shared organization")
	}
	if err := store.UpdateAs(op, func(a *Account) error {
		org := a.Organizations[op.OrganizationID]
		delete(org.Members, member.User.ID)
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	called := false
	if err := store.UpdateAs(mp, func(*Account) error { called = true; return nil }); !errors.Is(err, ErrForbidden) && !errors.Is(err, ErrNotFound) {
		t.Fatalf("revoked member mutation: %v", err)
	}
	if called {
		t.Fatal("revoked member callback ran")
	}
	repaired, _ := store.Snapshot(member.ID)
	if repaired.ActiveOrganizationID == op.OrganizationID || repaired.ActiveProjectID == op.ProjectID {
		t.Fatal("revoked member selection not repaired")
	}
}

func TestConcurrentUpdatesAndBillingReplay(t *testing.T) {
	store := NewStore()
	a := testAccount(t, store, "bill@example.com")
	p := a.Principal()
	var wg sync.WaitGroup
	for range 50 {
		wg.Go(func() {
			if err := store.UpdateAs(p, func(a *Account) error {
				org := a.Organizations[p.OrganizationID]
				org.Billing.SpendCapCents++
				a.Organizations[org.ID] = org
				return nil
			}); err != nil {
				t.Errorf("update: %v", err)
			}
		})
		wg.Go(func() {
			if _, err := store.SnapshotAs(p); err != nil {
				t.Errorf("snapshot: %v", err)
			}
		})
	}
	wg.Wait()
	got, _ := store.SnapshotAs(p)
	if got.Organizations[p.OrganizationID].Billing.SpendCapCents != 50 {
		t.Fatal("lost concurrent update")
	}
	if err := store.UpdateAs(p, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		org.Billing.CustomerID = "cus_1"
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	other := testAccount(t, store, "other-bill@example.com")
	op := other.Principal()
	if err := store.UpdateAs(op, func(a *Account) error {
		org := a.Organizations[op.OrganizationID]
		org.Billing.CustomerID = "cus_1"
		a.Organizations[org.ID] = org
		return nil
	}); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate provider identity: %v", err)
	}
	callback := func(org *Organization) error { org.Billing.Status = "active"; return nil }
	if err := store.ApplyBillingEvent("evt_1", "cus_1", "", callback); err != nil {
		t.Fatal(err)
	}
	if err := store.ApplyBillingEvent("evt_1", "cus_1", "", callback); !errors.Is(err, ErrAlreadyProcessed) {
		t.Fatalf("billing replay: %v", err)
	}
}

func TestUpdateAsWithPlanSerializesPlanDeletionAndAccountCommit(t *testing.T) {
	store := NewStore()
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin@example.com", PasswordHash: "hashed", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	adminPrincipal := admin.Principal()
	plan := Plan{ID: "plan_pending", Key: "pending", Name: "Pending", PriceCents: 500, Visible: true}
	if err := store.UpsertPlanAs(adminPrincipal, plan); err != nil {
		t.Fatal(err)
	}
	account := testAccount(t, store, "checkout@example.com")
	principal := account.Principal()
	entered := make(chan struct{})
	release := make(chan struct{})
	updateDone := make(chan error, 1)
	go func() {
		updateDone <- store.UpdateAsWithPlan(principal, plan.ID, func(account *Account, selected Plan) error {
			if selected.ID != plan.ID || !selected.Visible {
				return ErrInvalid
			}
			close(entered)
			<-release
			org := account.Organizations[principal.OrganizationID]
			org.PlanID = selected.ID
			account.Organizations[org.ID] = org
			return nil
		})
	}()
	<-entered
	deleteDone := make(chan error, 1)
	go func() {
		deleteDone <- store.DeletePlanAs(adminPrincipal, plan.ID)
	}()
	select {
	case err := <-deleteDone:
		close(release)
		t.Fatalf("plan deletion completed before account commit: %v", err)
	case <-time.After(20 * time.Millisecond):
	}
	close(release)
	if err := <-updateDone; err != nil {
		t.Fatalf("scoped plan update: %v", err)
	}
	if err := <-deleteDone; !errors.Is(err, ErrConflict) {
		t.Fatalf("deleting active plan: %v", err)
	}
	got, err := store.SnapshotAs(principal)
	if err != nil || got.Organizations[principal.OrganizationID].PlanID != plan.ID {
		t.Fatalf("committed plan: %q, %v", got.Organizations[principal.OrganizationID].PlanID, err)
	}

	called := false
	if err := store.UpdateAsWithPlan(principal, "plan_missing", func(*Account, Plan) error {
		called = true
		return nil
	}); !errors.Is(err, ErrNotFound) || called {
		t.Fatalf("missing plan: %v, callback called: %v", err, called)
	}
	forged := principal
	forged.UserID = admin.User.ID
	if err := store.UpdateAsWithPlan(forged, "plan_missing", func(*Account, Plan) error {
		called = true
		return nil
	}); !errors.Is(err, ErrForbidden) || called {
		t.Fatalf("unauthorized plan lookup: %v, callback called: %v", err, called)
	}
}

func TestApplyBillingEventWithPlansSerializesCatalogAndProjection(t *testing.T) {
	store := NewStore()
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin-webhook@example.com", PasswordHash: "hashed", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	adminPrincipal := admin.Principal()
	plan := Plan{ID: "plan_webhook", Key: "webhook", Name: "Webhook", PriceCents: 500, Visible: true}
	if err := store.UpsertPlanAs(adminPrincipal, plan); err != nil {
		t.Fatal(err)
	}
	account := testAccount(t, store, "webhook@example.com")
	principal := account.Principal()
	if err := store.UpdateAs(principal, func(account *Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.Billing.CustomerID = "cus_webhook"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	entered := make(chan struct{})
	release := make(chan struct{})
	eventDone := make(chan error, 1)
	go func() {
		eventDone <- store.ApplyBillingEventWithPlans("evt_webhook", "cus_webhook", "", func(org *Organization, plans map[string]Plan) error {
			selected, ok := plans[plan.ID]
			if !ok || selected.Key != plan.Key {
				return ErrNotFound
			}
			close(entered)
			<-release
			org.PlanID = selected.ID
			return nil
		})
	}()
	<-entered
	deleteDone := make(chan error, 1)
	go func() { deleteDone <- store.DeletePlanAs(adminPrincipal, plan.ID) }()
	select {
	case err := <-deleteDone:
		close(release)
		t.Fatalf("plan deletion completed before event commit: %v", err)
	case <-time.After(20 * time.Millisecond):
	}
	close(release)
	if err := <-eventDone; err != nil {
		t.Fatalf("event projection: %v", err)
	}
	if err := <-deleteDone; !errors.Is(err, ErrConflict) {
		t.Fatalf("deleting event-activated plan: %v", err)
	}
	if err := store.ApplyBillingEventWithPlans("evt_webhook", "cus_webhook", "", func(*Organization, map[string]Plan) error { return nil }); !errors.Is(err, ErrAlreadyProcessed) {
		t.Fatalf("event replay: %v", err)
	}

	const retryID = "plan_retry"
	project := func(org *Organization, plans map[string]Plan) error {
		selected, ok := plans[retryID]
		if !ok {
			return ErrNotFound
		}
		org.PlanID = selected.ID
		return nil
	}
	if err := store.ApplyBillingEventWithPlans("evt_retry", "cus_webhook", "", project); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing plan event: %v", err)
	}
	if err := store.UpsertPlanAs(adminPrincipal, Plan{ID: retryID, Key: "retry", Visible: true}); err != nil {
		t.Fatal(err)
	}
	if err := store.ApplyBillingEventWithPlans("evt_retry", "cus_webhook", "", project); err != nil {
		t.Fatalf("retry after plan restored: %v", err)
	}
	got, err := store.SnapshotAs(principal)
	if err != nil || got.Organizations[principal.OrganizationID].PlanID != retryID {
		t.Fatalf("retried event plan: %q, %v", got.Organizations[principal.OrganizationID].PlanID, err)
	}
}

func TestDeletePlanProtectsOpenCheckoutUntilTerminal(t *testing.T) {
	for _, release := range []string{"canceled", "expired", "demo_expired"} {
		t.Run(release, func(t *testing.T) {
			store := NewStore()
			admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin-" + release + "@example.com", PasswordHash: "hashed", PlatformAdmin: true})
			if err != nil {
				t.Fatal(err)
			}
			adminPrincipal := admin.Principal()
			plan := Plan{ID: "plan_" + release, Key: release, Name: release, PriceCents: 500, Visible: true}
			if err := store.UpsertPlanAs(adminPrincipal, plan); err != nil {
				t.Fatal(err)
			}
			account := testAccount(t, store, "customer-"+release+"@example.com")
			principal := account.Principal()
			if err := store.UpdateAs(principal, func(account *Account) error {
				org := account.Organizations[principal.OrganizationID]
				org.CheckoutSessions["cs_pending"] = CheckoutSession{
					ID: "cs_pending", OrganizationID: org.ID, OwnerUserID: principal.UserID,
					PlanID: plan.ID, Status: "open", Demo: release == "demo_expired", ExpiresAt: time.Now().Add(time.Hour),
				}
				account.Organizations[org.ID] = org
				return nil
			}); err != nil {
				t.Fatal(err)
			}
			if err := store.DeletePlanAs(adminPrincipal, plan.ID); !errors.Is(err, ErrConflict) {
				t.Fatalf("delete plan with payable checkout: %v", err)
			}
			if err := store.UpdateAs(principal, func(account *Account) error {
				org := account.Organizations[principal.OrganizationID]
				session := org.CheckoutSessions["cs_pending"]
				if release == "canceled" {
					session.Status = "canceled"
				} else {
					session.ExpiresAt = time.Now().Add(-time.Minute)
				}
				org.CheckoutSessions[session.ID] = session
				account.Organizations[org.ID] = org
				return nil
			}); err != nil {
				t.Fatal(err)
			}
			if release == "expired" {
				if err := store.DeletePlanAs(adminPrincipal, plan.ID); !errors.Is(err, ErrConflict) {
					t.Fatalf("delete plan with expired but unreconciled checkout: %v", err)
				}
				if err := store.UpdateAs(principal, func(account *Account) error {
					org := account.Organizations[principal.OrganizationID]
					session := org.CheckoutSessions["cs_pending"]
					session.Status = "canceled"
					org.CheckoutSessions[session.ID] = session
					account.Organizations[org.ID] = org
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}
			if err := store.DeletePlanAs(adminPrincipal, plan.ID); err != nil {
				t.Fatalf("delete plan after checkout %s: %v", release, err)
			}
		})
	}
}

func TestPlanKeyCannotChangeWhilePendingCheckoutReferencesItsID(t *testing.T) {
	store := NewStore()
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin-plan-key@example.com", PasswordHash: "hashed", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	adminPrincipal := admin.Principal()
	plan := Plan{ID: "plan_custom", Key: "custom", Name: "Custom", PriceCents: 500, Visible: true}
	if err := store.UpsertPlanAs(adminPrincipal, plan); err != nil {
		t.Fatal(err)
	}
	account := testAccount(t, store, "pending-plan-key@example.com")
	principal := account.Principal()
	if err := store.UpdateAs(principal, func(account *Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.CheckoutSessions["cs_pending"] = CheckoutSession{
			ID: "cs_pending", OrganizationID: org.ID, OwnerUserID: principal.UserID,
			PlanID: plan.ID, Status: "open", ExpiresAt: time.Now().Add(time.Hour),
		}
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	renamed := plan
	renamed.Key = "renamed"
	if err := store.UpsertPlanAs(adminPrincipal, renamed); !errors.Is(err, ErrConflict) {
		t.Fatalf("renaming a plan key: %v", err)
	}
	if got, ok := store.Plan(plan.ID); !ok || got.Key != plan.Key {
		t.Fatalf("pending plan key changed: %#v, found %v", got, ok)
	}
	updated := plan
	updated.Name = "Updated custom"
	updated.PriceCents = 750
	updated.Visible = false
	if err := store.UpsertPlanAs(adminPrincipal, updated); err != nil {
		t.Fatalf("updating non-identity fields: %v", err)
	}
	if got, ok := store.Plan(plan.Key); !ok || got.Name != updated.Name || got.PriceCents != updated.PriceCents || got.Visible {
		t.Fatalf("editable plan fields were not updated: %#v, found %v", got, ok)
	}
	if err := store.UpsertPlanAs(adminPrincipal, Plan{ID: "plan_other", Key: plan.Key, Visible: true}); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate plan key: %v", err)
	}
}
