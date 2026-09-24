package subscriptions

import (
	"errors"
	"slices"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/features/payments"
)

func TestDemoCheckoutActivatesChosenPlanAndConsumesSession(t *testing.T) {
	store, principal := newBusinessAccount(t)
	now := time.Date(2026, time.July, 1, 12, 0, 0, 0, time.UTC)
	service := BillingService{store: store, now: func() time.Time { return now }}
	session, err := service.BeginDemoCheckout(principal, "pro", intervalAnnual, "/organization?newOrganization=project")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	if session.Status != checkoutOpen || session.OrganizationID != principal.OrganizationID || session.OwnerUserID != principal.UserID {
		t.Fatalf("checkout session was not scoped to the owner tenant: %#v", session)
	}

	returnTo, err := service.CompleteDemoCheckout(principal, session.ID, false)
	if err != nil {
		t.Fatalf("CompleteDemoCheckout() error = %v", err)
	}
	if returnTo != "/organization?newOrganization=project&payment=success" {
		t.Fatalf("return path = %q", returnTo)
	}

	account, ok := store.Snapshot(principal.AccountID)
	if !ok {
		t.Fatal("account was lost")
	}
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != "plan_pro" || organization.Billing.PlanKey != "pro" || organization.Billing.Interval != intervalAnnual {
		t.Fatalf("selected plan was not applied: %#v", organization)
	}
	if organization.BillingStatus != "active" || organization.Billing.Status != "active" || !organization.PaymentMethodValidated {
		t.Fatalf("demo checkout did not activate billing state: %#v", organization)
	}
	if !organization.BillingRenewal.Equal(now.AddDate(1, 0, 0)) {
		t.Fatalf("annual renewal = %s", organization.BillingRenewal)
	}
	if organization.Billing.CheckoutID != "" || organization.Billing.ReturnPath != "" || organization.CheckoutSessions[session.ID].Status != checkoutDone {
		t.Fatalf("checkout state was not consumed: billing=%#v session=%#v", organization.Billing, organization.CheckoutSessions[session.ID])
	}
	if _, err := service.CompleteDemoCheckout(principal, session.ID, false); !errors.Is(err, ErrCheckoutUnavailable) {
		t.Fatalf("replayed completion error = %v", err)
	}
}

func TestDemoCheckoutCancelLeavesBillingUnchangedAndConsumesSession(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(principal, "starter", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	returnTo, err := service.CompleteDemoCheckout(principal, session.ID, true)
	if err != nil {
		t.Fatalf("cancel checkout error = %v", err)
	}
	if returnTo != "/billing?payment=canceled" {
		t.Fatalf("cancel return path = %q", returnTo)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.BillingStatus != "billing_required" || organization.PaymentMethodValidated || organization.PlanID != "plan_starter" {
		t.Fatalf("cancel unexpectedly changed billing: %#v", organization)
	}
	if organization.CheckoutSessions[session.ID].Status != checkoutCancel || organization.Billing.CheckoutID != "" {
		t.Fatalf("cancel did not consume checkout: %#v", organization)
	}
}

func TestDemoCheckoutUpgradeCancelPreservesActivePlanAndReplacedSession(t *testing.T) {
	store, principal := newBusinessAccount(t)
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		organization := account.Organizations[principal.OrganizationID]
		organization.PlanID = "plan_pro"
		organization.Billing.PlanKey = "pro"
		organization.Billing.Interval = intervalMonthly
		organization.Billing.Status = "active"
		organization.BillingStatus = "active"
		organization.PaymentMethodValidated = true
		account.Organizations[organization.ID] = organization
		return nil
	}); err != nil {
		t.Fatalf("prepare active billing: %v", err)
	}
	service := NewBillingService(store)
	first, err := service.BeginDemoCheckout(principal, "business", intervalAnnual, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout(first) error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != "plan_pro" || organization.Billing.PlanKey != "pro" {
		t.Fatalf("pending checkout changed current plan before completion: %#v", organization.Billing)
	}
	second, err := service.BeginDemoCheckout(principal, "starter", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout(replacement) error = %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, first.ID, false); !errors.Is(err, ErrCheckoutUnavailable) {
		t.Fatalf("replaced checkout completion error = %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, second.ID, true); err != nil {
		t.Fatalf("cancel replacement checkout: %v", err)
	}
	account, _ = store.Snapshot(principal.AccountID)
	organization = account.Organizations[principal.OrganizationID]
	if organization.PlanID != "plan_pro" || organization.Billing.PlanKey != "pro" || organization.BillingStatus != "active" || !organization.PaymentMethodValidated {
		t.Fatalf("canceling pending upgrade altered active billing: %#v", organization)
	}
}

func TestDemoCheckoutProtectsItsPlanUntilTheSessionIsConsumed(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(principal, "business", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	adminAccount, err := store.CreateAccount(core.User{
		Email: "billing-admin@example.test", Name: "Billing Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	if err := store.DeletePlanAs(adminAccount.Principal(), "plan_business"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("DeletePlanAs(open checkout plan) error = %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, session.ID, false); err != nil {
		t.Fatalf("completion after protected deletion: %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != session.PlanID || organization.BillingStatus != "active" || organization.CheckoutSessions[session.ID].Status != checkoutDone {
		t.Fatalf("protected plan checkout did not complete: %#v", organization)
	}
}

func TestDemoCheckoutCanCompleteAfterPlanIsHidden(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(principal, "pro", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	admin, err := store.CreateAccount(core.User{
		Email: "hidden-demo-admin@example.test", Name: "Hidden Demo Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	plan, ok := store.Plan(session.PlanID)
	if !ok {
		t.Fatalf("Plan(%q) did not find checkout plan", session.PlanID)
	}
	plan.Visible = false
	if err := store.UpsertPlanAs(admin.Principal(), plan); err != nil {
		t.Fatalf("hide checkout plan: %v", err)
	}
	if _, err := service.BeginDemoCheckout(principal, "pro", intervalMonthly, "/billing"); !errors.Is(err, ErrPlanUnavailable) {
		t.Fatalf("new hidden-plan checkout error = %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, session.ID, false); err != nil {
		t.Fatalf("complete existing hidden-plan checkout: %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != plan.ID || organization.BillingStatus != "active" {
		t.Fatalf("hidden plan was not applied to the existing checkout: %#v", organization.Billing)
	}
}

func TestDemoCheckoutDoesNotRetargetAfterOrganizationSwitch(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(principal, "starter", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		for id, org := range account.Organizations {
			if org.Kind == "personal" {
				account.ActiveOrganizationID = id
				return nil
			}
		}
		return core.ErrNotFound
	}); err != nil {
		t.Fatalf("switch active organization: %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, session.ID, false); !errors.Is(err, ErrCheckoutUnavailable) {
		t.Fatalf("checkout after organization switch error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.BillingStatus != "billing_required" || organization.PaymentMethodValidated {
		t.Fatalf("checkout from another active organization changed tenant billing: %#v", organization)
	}
}

func TestDemoCheckoutRejectsWrongOwnerAndExpiredSession(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(principal, "business", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}

	memberPrincipal := principal
	memberPrincipal.UserID = "usr_other"
	if _, err := service.Checkout(memberPrincipal, session.ID); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("non-owner lookup error = %v", err)
	}

	service.now = func() time.Time { return session.ExpiresAt }
	if _, err := service.Checkout(principal, session.ID); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("expired lookup error = %v", err)
	}
}

func TestNormalizeReturnPathRejectsExternalAndProtocolRelativeTargets(t *testing.T) {
	for _, value := range []string{
		"https://attacker.example/path",
		"//attacker.example/path",
		"/%2f%2fattacker.example/path",
		`/\\attacker.example/path`,
		"/billing\r\nLocation: https://attacker.example",
	} {
		t.Run(value, func(t *testing.T) {
			if _, err := NormalizeReturnPath(value, "/billing"); !errors.Is(err, ErrInvalidReturnPath) {
				t.Fatalf("NormalizeReturnPath(%q) error = %v", value, err)
			}
		})
	}
	if path, err := NormalizeReturnPath("/organization?newOrganization=project", "/billing"); err != nil || path != "/organization?newOrganization=project" {
		t.Fatalf("valid app path = %q, error = %v", path, err)
	}
}

func TestBeginDemoCheckoutRejectsHiddenFreeOrUnknownPlan(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	for _, plan := range []string{"free", "enterprise", "unknown"} {
		if _, err := service.BeginDemoCheckout(principal, plan, intervalMonthly, "/billing"); !errors.Is(err, ErrPlanUnavailable) {
			t.Errorf("BeginDemoCheckout(%q) error = %v", plan, err)
		}
	}
	if _, err := service.BeginDemoCheckout(principal, "pro", "weekly", "/billing"); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("unsupported interval error = %v", err)
	}
}

func TestDemoCheckoutSupportsAnyValidVisibleCatalogPlan(t *testing.T) {
	store, principal := newBusinessAccount(t)
	admin, err := store.CreateAccount(core.User{
		Email: "catalog-admin@example.test", Name: "Catalog Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	plan := core.Plan{
		ID: "plan_team", Key: "team", Name: "Team", PriceCents: 1500,
		TrialDays: 7, StorageMiB: 1024, MonthlyRequests: 50000, Visible: true,
	}
	if err := store.UpsertPlanAs(admin.Principal(), plan); err != nil {
		t.Fatalf("UpsertPlanAs(team) error = %v", err)
	}
	service := NewBillingService(store)
	snapshot, err := service.Snapshot(principal)
	if err != nil {
		t.Fatalf("Snapshot() error = %v", err)
	}
	if slices.IndexFunc(snapshot.Plans, func(candidate core.Plan) bool { return candidate.ID == plan.ID }) < 0 {
		t.Fatalf("monthly-only catalog plan missing from available plans: %#v", snapshot.Plans)
	}
	if _, err := service.BeginDemoCheckout(principal, plan.Key, intervalAnnual, "/billing"); !errors.Is(err, ErrPlanUnavailable) {
		t.Fatalf("monthly-only annual checkout error = %v", err)
	}
	session, err := service.BeginDemoCheckout(principal, plan.Key, intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout(team) error = %v", err)
	}
	if _, err := service.CompleteDemoCheckout(principal, session.ID, false); err != nil {
		t.Fatalf("CompleteDemoCheckout(team) error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != plan.ID || organization.Billing.PlanKey != plan.Key || organization.Billing.Interval != intervalMonthly || organization.BillingStatus != "active" {
		t.Fatalf("custom catalog plan was not activated: %#v", organization.Billing)
	}
}

func TestStripeCheckoutReturnNeverActivatesBilling(t *testing.T) {
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	if _, err := service.RecordCustomer(principal, "cus_return"); err != nil {
		t.Fatalf("RecordCustomer() error = %v", err)
	}
	remote := payments.CheckoutSession{
		ID: "cs_return", CustomerID: "cus_return", ClientReferenceID: principal.OrganizationID,
		Status: "complete", PaymentStatus: "paid",
	}
	if err := service.RecordExternalCheckout(principal, "pro", intervalMonthly, "/billing", remote); err != nil {
		t.Fatalf("RecordExternalCheckout() error = %v", err)
	}
	returnTo, status, err := service.ReconcileCheckoutReturn(principal, remote.ID, remote)
	if err != nil {
		t.Fatalf("ReconcileCheckoutReturn() error = %v", err)
	}
	if returnTo != "/billing" || status != "processing" {
		t.Fatalf("checkout return = (%q, %q)", returnTo, status)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.BillingStatus != "billing_required" || organization.Billing.SubscriptionID != "" || organization.PaymentMethodValidated {
		t.Fatalf("browser return activated billing: %#v", organization.Billing)
	}
}

func TestStripeWebhookActivatesOnlyPaidBoundCheckoutAndDeduplicates(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	createdAt := time.Date(2026, time.July, 1, 12, 0, 0, 0, time.UTC)
	base := payments.BillingEvent{
		ID: "evt_paid", Type: "checkout.session.completed", CreatedAt: createdAt,
		CheckoutID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, CheckoutStatus: "complete", PaymentStatus: "paid",
	}
	if err := service.ApplyStripeEvent(base); err != nil {
		t.Fatalf("ApplyStripeEvent() error = %v", err)
	}
	if err := service.ApplyStripeEvent(base); !errors.Is(err, core.ErrAlreadyProcessed) {
		t.Fatalf("duplicate event error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != "plan_pro" || organization.BillingStatus != "active" || organization.Billing.Status != "active" || organization.Billing.SubscriptionID != "sub_external" || !organization.PaymentMethodValidated {
		t.Fatalf("paid webhook did not activate selected subscription: %#v", organization)
	}
	if organization.CheckoutSessions[pending.ID].Status != checkoutDone || organization.Billing.CheckoutID != "" {
		t.Fatalf("paid webhook did not consume pending checkout: %#v", organization.CheckoutSessions[pending.ID])
	}
	remote := payments.CheckoutSession{
		ID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, Status: "complete", PaymentStatus: "paid",
	}
	if _, err := service.CheckoutForReturn(principal, pending.ID); err != nil {
		t.Fatalf("return lookup after webhook consumption: %v", err)
	}
	returnTo, status, err := service.ReconcileCheckoutReturn(principal, pending.ID, remote)
	if err != nil || returnTo != "/billing" || status != "success" {
		t.Fatalf("return after webhook = (%q, %q, %v)", returnTo, status, err)
	}
}

func TestExternalCheckoutCannotReplaceAnOpenSession(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		organization := account.Organizations[principal.OrganizationID]
		session := organization.CheckoutSessions[pending.ID]
		session.ExpiresAt = time.Now().Add(-time.Hour)
		organization.CheckoutSessions[pending.ID] = session
		account.Organizations[organization.ID] = organization
		return nil
	}); err != nil {
		t.Fatalf("expire local checkout clock: %v", err)
	}
	second := payments.CheckoutSession{
		ID: "cs_second", CustomerID: "cus_external", ClientReferenceID: principal.OrganizationID,
	}
	if err := service.RecordExternalCheckout(principal, "starter", intervalMonthly, "/billing", second); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("RecordExternalCheckout(second) error = %v, want conflict", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.Billing.CheckoutID != pending.ID || len(organization.CheckoutSessions) != 1 || organization.CheckoutSessions[pending.ID].Status != checkoutOpen {
		t.Fatalf("second checkout replaced the open binding: %#v", organization)
	}
	if err := service.ExpireExternalCheckout(principal, pending.ID); err != nil {
		t.Fatalf("expire provider-confirmed checkout: %v", err)
	}
	if err := service.RecordExternalCheckout(principal, "starter", intervalMonthly, "/billing", second); err != nil {
		t.Fatalf("RecordExternalCheckout(after expiration) error = %v", err)
	}
}

func TestStripeWebhookHonorsAnOpenCheckoutAfterPlanIsHidden(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	admin, err := store.CreateAccount(core.User{
		Email: "hidden-plan-admin@example.test", Name: "Hidden Plan Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	plan, ok := store.Plan(pending.PlanID)
	if !ok {
		t.Fatalf("Plan(%q) did not find checkout plan", pending.PlanID)
	}
	plan.Visible = false
	if err := store.UpsertPlanAs(admin.Principal(), plan); err != nil {
		t.Fatalf("hide checkout plan: %v", err)
	}
	event := payments.BillingEvent{
		ID: "evt_hidden_checkout_plan", Type: "checkout.session.completed", CreatedAt: time.Now().UTC(),
		CheckoutID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, CheckoutStatus: "complete", PaymentStatus: "paid",
	}
	if err := service.ApplyStripeEvent(event); err != nil {
		t.Fatalf("ApplyStripeEvent(hidden pending plan) error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.PlanID != plan.ID || organization.BillingStatus != "active" {
		t.Fatalf("hidden plan was not granted for the existing paid checkout: %#v", organization.Billing)
	}
}

func TestPaidCheckoutProjectionRequiresItsBoundPlan(t *testing.T) {
	_, principal, _, pending := newExternalCheckout(t)
	organization := core.Organization{
		ID:               principal.OrganizationID,
		Billing:          core.Billing{CheckoutID: pending.ID},
		CheckoutSessions: map[string]core.CheckoutSession{pending.ID: pending},
	}
	event := payments.BillingEvent{
		ID: "evt_checkout_missing_plan", Type: "checkout.session.completed", CreatedAt: time.Now().UTC(),
		CheckoutID: pending.ID, SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, PlanKey: "pro", Interval: intervalAnnual,
		CheckoutStatus: "complete", PaymentStatus: "paid",
	}
	if err := applyCheckoutEvent(nil, &organization, event, event.CreatedAt); !errors.Is(err, ErrPlanUnavailable) {
		t.Fatalf("applyCheckoutEvent(missing plan) error = %v", err)
	}
}

func TestStripeWebhookRetriesWhenRequiredSubscriptionPlanIsDeleted(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	createdAt := time.Now().UTC()
	if err := service.ApplyStripeEvent(payments.BillingEvent{
		ID: "evt_initial_subscription", Type: "checkout.session.completed", CreatedAt: createdAt,
		CheckoutID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, CheckoutStatus: "complete", PaymentStatus: "paid",
	}); err != nil {
		t.Fatalf("activate initial subscription: %v", err)
	}
	admin, err := store.CreateAccount(core.User{
		Email: "subscription-admin@example.test", Name: "Subscription Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	plan, ok := store.Plan("starter")
	if !ok {
		t.Fatal("Plan(starter) did not find subscription update plan")
	}
	if err := store.DeletePlanAs(admin.Principal(), plan.ID); err != nil {
		t.Fatalf("DeletePlanAs() error = %v", err)
	}
	event := payments.BillingEvent{
		ID: "evt_subscription_plan_retry", Type: "customer.subscription.updated", CreatedAt: createdAt,
		ObservedAt: createdAt.Add(time.Minute), ProviderCurrent: true,
		CustomerID: "cus_external", SubscriptionID: "sub_external", OrganizationID: principal.OrganizationID,
		PlanKey: "starter", Interval: intervalMonthly, SubscriptionState: "active",
	}
	if err := service.ApplyStripeEvent(event); !errors.Is(err, ErrPlanUnavailable) {
		t.Fatalf("ApplyStripeEvent(missing subscription plan) error = %v", err)
	}
	if err := store.UpsertPlanAs(admin.Principal(), plan); err != nil {
		t.Fatalf("restore subscription plan: %v", err)
	}
	if err := service.ApplyStripeEvent(event); err != nil {
		t.Fatalf("retry ApplyStripeEvent() after restoring plan: %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	if got := account.Organizations[principal.OrganizationID].PlanID; got != plan.ID {
		t.Fatalf("retried subscription plan = %q, want %q", got, plan.ID)
	}
}

func TestStripeWebhookRejectsUnpaidMismatchedAndStaleEvents(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	createdAt := time.Date(2026, time.July, 1, 12, 0, 0, 0, time.UTC)
	base := payments.BillingEvent{
		ID: "evt_unpaid", Type: "checkout.session.completed", CreatedAt: createdAt,
		CheckoutID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, CheckoutStatus: "complete", PaymentStatus: "unpaid",
	}
	if err := service.ApplyStripeEvent(base); err != nil {
		t.Fatalf("unpaid ApplyStripeEvent() error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	organization := account.Organizations[principal.OrganizationID]
	if organization.BillingStatus != "billing_required" || organization.Billing.CheckoutID != pending.ID {
		t.Fatalf("unpaid checkout changed entitlement or pending session: %#v", organization.Billing)
	}

	paid := base
	paid.ID = "evt_paid_after_unpaid"
	paid.CreatedAt = createdAt.Add(time.Second)
	paid.PaymentStatus = "paid"
	if err := service.ApplyStripeEvent(paid); err != nil {
		t.Fatalf("paid ApplyStripeEvent() error = %v", err)
	}

	stale := payments.BillingEvent{
		ID: "evt_old_past_due", Type: "customer.subscription.updated", CreatedAt: createdAt,
		CustomerID: "cus_external", SubscriptionID: "sub_external", OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, SubscriptionState: "past_due",
	}
	if err := service.ApplyStripeEvent(stale); err != nil {
		t.Fatalf("stale ApplyStripeEvent() error = %v", err)
	}

	account, _ = store.Snapshot(principal.AccountID)
	organization = account.Organizations[principal.OrganizationID]
	if organization.BillingStatus != "active" || organization.Billing.Status != "active" {
		t.Fatalf("stale event downgraded newer paid state: %#v", organization.Billing)
	}

	newerWrongCheckout := paid
	newerWrongCheckout.ID = "evt_wrong_checkout"
	newerWrongCheckout.CreatedAt = createdAt.Add(2 * time.Second)
	newerWrongCheckout.CheckoutID = "cs_other_tenant"
	if err := service.ApplyStripeEvent(newerWrongCheckout); err != nil {
		t.Fatalf("mismatched checkout event error = %v", err)
	}
	account, _ = store.Snapshot(principal.AccountID)
	if got := account.Organizations[principal.OrganizationID].Billing.SubscriptionID; got != "sub_external" {
		t.Fatalf("mismatched checkout changed subscription ID to %q", got)
	}
}

func TestStripeSubscriptionSnapshotOrdersEqualTimestampEventsAndAppliesPortalPrice(t *testing.T) {
	store, principal, service, pending := newExternalCheckout(t)
	createdAt := time.Date(2026, time.July, 1, 12, 0, 0, 0, time.UTC)
	observedAt := createdAt.Add(2 * time.Hour)
	checkout := payments.BillingEvent{
		ID: "evt_checkout_snapshot", Type: "checkout.session.completed", CreatedAt: createdAt,
		ObservedAt: observedAt, ProviderCurrent: true,
		CheckoutID: pending.ID, CustomerID: "cus_external", SubscriptionID: "sub_external",
		ClientReferenceID: principal.OrganizationID, OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, CheckoutStatus: "complete", PaymentStatus: "paid",
		SubscriptionState: "active", PeriodEnd: createdAt.AddDate(1, 0, 0),
	}
	if err := service.ApplyStripeEvent(checkout); err != nil {
		t.Fatalf("checkout snapshot event error = %v", err)
	}

	canceled := payments.BillingEvent{
		ID: "evt_canceled_snapshot", Type: "customer.subscription.deleted", CreatedAt: createdAt,
		ObservedAt: observedAt.Add(time.Second), ProviderCurrent: true,
		CustomerID: "cus_external", SubscriptionID: "sub_external", OrganizationID: principal.OrganizationID,
		PlanKey: "pro", Interval: intervalAnnual, SubscriptionState: "canceled",
	}
	if err := service.ApplyStripeEvent(canceled); err != nil {
		t.Fatalf("canceled snapshot event error = %v", err)
	}
	lateActive := canceled
	lateActive.ID = "evt_late_active_snapshot"
	lateActive.Type = "customer.subscription.updated"
	lateActive.ObservedAt = observedAt.Add(500 * time.Millisecond)
	lateActive.SubscriptionState = "active"
	if err := service.ApplyStripeEvent(lateActive); err != nil {
		t.Fatalf("late active snapshot event error = %v", err)
	}
	account, _ := store.Snapshot(principal.AccountID)
	org := account.Organizations[principal.OrganizationID]
	if org.BillingStatus != "billing_required" || org.Billing.Status != "canceled" {
		t.Fatalf("late equal-Stripe-time active event overrode newer provider snapshot: %#v", org.Billing)
	}

	// A later provider snapshot reflects a portal downgrade by current Price ID,
	// independent of the plan metadata written at original checkout.
	service = NewBillingService(store)
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.Billing.Status = "active"
		org.BillingStatus = "active"
		org.Billing.LastEventAt = observedAt.Add(2 * time.Second)
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("restore active state for portal price event: %v", err)
	}
	downgrade := payments.BillingEvent{
		ID: "evt_portal_downgrade", Type: "customer.subscription.updated", CreatedAt: createdAt,
		ObservedAt: observedAt.Add(3 * time.Second), ProviderCurrent: true,
		CustomerID: "cus_external", SubscriptionID: "sub_external", OrganizationID: principal.OrganizationID,
		PlanKey: "starter", Interval: intervalMonthly, SubscriptionState: "active",
	}
	admin, err := store.CreateAccount(core.User{
		Email: "portal-plan-admin@example.test", Name: "Portal Plan Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	starter, ok := store.Plan("starter")
	if !ok {
		t.Fatal("Plan(starter) did not find portal plan")
	}
	starter.Visible = false
	if err := store.UpsertPlanAs(admin.Principal(), starter); err != nil {
		t.Fatalf("hide portal plan: %v", err)
	}
	if err := service.ApplyStripeEvent(downgrade); err != nil {
		t.Fatalf("portal price update error = %v", err)
	}
	account, _ = store.Snapshot(principal.AccountID)
	org = account.Organizations[principal.OrganizationID]
	if org.PlanID != "plan_starter" || org.Billing.PlanKey != "starter" || org.Billing.Interval != intervalMonthly || org.BillingStatus != "active" {
		t.Fatalf("current provider price did not determine plan entitlement: %#v", org.Billing)
	}
}

func newExternalCheckout(t *testing.T) (*core.Store, core.Principal, BillingService, core.CheckoutSession) {
	t.Helper()
	store, principal := newBusinessAccount(t)
	service := NewBillingService(store)
	if _, err := service.RecordCustomer(principal, "cus_external"); err != nil {
		t.Fatalf("RecordCustomer() error = %v", err)
	}
	remote := payments.CheckoutSession{ID: "cs_external", CustomerID: "cus_external", ClientReferenceID: principal.OrganizationID}
	if err := service.RecordExternalCheckout(principal, "pro", intervalAnnual, "/billing", remote); err != nil {
		t.Fatalf("RecordExternalCheckout() error = %v", err)
	}
	session, err := service.Checkout(principal, remote.ID)
	if err != nil {
		t.Fatalf("Checkout() error = %v", err)
	}
	return store, principal, service, session
}

func newBusinessAccount(t *testing.T) (*core.Store, core.Principal) {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		Email:        "billing@example.test",
		Name:         "Billing Owner",
		PasswordHash: "test-hash",
		Role:         "owner",
	})
	if err != nil {
		t.Fatalf("CreateAccount() error = %v", err)
	}
	created, err := organization.NewService(store).CreateBusiness(account.Principal(), "Billing Labs")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	current, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("Snapshot() did not find created account")
	}
	principal := current.Principal()
	if principal.OrganizationID != created.ID {
		t.Fatalf("active organization = %q, want %q", principal.OrganizationID, created.ID)
	}
	return store, principal
}
