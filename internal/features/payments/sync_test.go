package payments_test

import (
	"context"
	"fmt"
	"net/http"
	"reflect"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

func TestCheckoutReturnSyncsBeforeWebhooksArrive(t *testing.T) {
	fixture := newBillingFixture(t)
	response := fixture.request(http.MethodGet, "/billing/checkout/return?session_id=cs_test")
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/usage?payment=success&tab=billing" {
		t.Fatalf("return = %d %q", response.Code, response.Header().Get("Location"))
	}
	if org := fixture.organization(t); org.PlanID != "plan_pro" || org.Billing.Status != "active" {
		t.Fatalf("billing after return = %#v", org.Billing)
	}
}

func TestCheckoutReturnReportsProcessingUntilPaymentConfirms(t *testing.T) {
	fixture := newBillingFixture(t)
	pending := activeSubscription("price_pro_monthly")
	pending.Status = "incomplete"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{pending}
	fixture.provider.checkout.Status, fixture.provider.checkout.PaymentStatus = "complete", "unpaid"
	response := fixture.request(http.MethodGet, "/billing/checkout/return?session_id=cs_test")
	if response.Header().Get("Location") != "/usage?payment=processing&tab=billing" {
		t.Fatalf("return = %q", response.Header().Get("Location"))
	}
	org := fixture.organization(t)
	if org.PlanID != "plan_free" || org.Billing.CheckoutID != "cs_test" {
		t.Fatalf("unconfirmed payment changed the plan or released checkout: %#v", org.Billing)
	}
}

func TestSyncIgnoresOlderStateProjectedLate(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.sync(t)
	newer := fixture.organization(t)
	stale := payments.CustomerState{
		CustomerID: "cus_test", OrganizationID: fixture.principal.OrganizationID, Status: payments.StatusNone,
		SyncedAt: newer.Billing.SyncedAt.Add(-time.Second),
	}
	if err := fixture.service.ProjectCustomerState(t.Context(), stale, nil); err != nil {
		t.Fatal(err)
	}
	if org := fixture.organization(t); !reflect.DeepEqual(org, newer) {
		t.Fatalf("older state replaced newer billing:\n got %#v\nwant %#v", org.Billing, newer.Billing)
	}
}

func TestSyncReleasesExpiredCheckout(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.provider.customer.Subscriptions = nil
	fixture.provider.checkout.Status, fixture.provider.checkout.SubscriptionID = "expired", ""
	fixture.sync(t)
	org := fixture.organization(t)
	if org.Billing.CheckoutID != "" || org.CheckoutSessions["cs_test"].Status != "canceled" || org.Billing.Status != payments.StatusNone || org.PlanID != "plan_free" {
		t.Fatalf("billing after expiry = %#v, sessions = %#v", org.Billing, org.CheckoutSessions)
	}
}

func TestSyncProjectsScheduledPendingAndCanceledChanges(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.sync(t)
	version := fixture.organization(t).Billing.ChangeVersion

	scheduled := activeSubscription("price_pro_monthly")
	scheduled.ScheduleID, scheduled.ScheduledPriceID = "sub_sched_test", "price_starter_monthly"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{scheduled}
	fixture.sync(t)
	fixture.sync(t)
	org := fixture.organization(t)
	if org.Billing.ScheduledPlanKey != payments.PlanStarter || org.Billing.ScheduleID != "sub_sched_test" || !org.Billing.ScheduledAt.Equal(testPeriodEnd) || org.PlanID != "plan_pro" {
		t.Fatalf("scheduled change = %#v", org.Billing)
	}
	if org.Billing.ChangeVersion != version+1 {
		t.Fatalf("change version = %d, want %d once per change", org.Billing.ChangeVersion, version+1)
	}

	pending := activeSubscription("price_pro_monthly")
	pending.PendingPriceID = "price_business_monthly"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{pending}
	fixture.sync(t)
	if org := fixture.organization(t); org.Billing.PendingPlanKey != payments.PlanBusiness || org.Billing.ScheduledPlanKey != "" {
		t.Fatalf("pending change = %#v", org.Billing)
	}

	canceling := activeSubscription("price_pro_monthly")
	canceling.CancelAtPeriodEnd = true
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{canceling}
	fixture.sync(t)
	if org := fixture.organization(t); org.Billing.ScheduledPlanKey != "free" || org.PlanID != "plan_pro" {
		t.Fatalf("scheduled cancellation = %#v", org.Billing)
	}

	canceled := activeSubscription("price_pro_monthly")
	canceled.Status = "canceled"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{canceled}
	fixture.sync(t)
	if org := fixture.organization(t); org.PlanID != "plan_free" || org.Billing.PlanKey != "free" || org.Billing.ScheduledPlanKey != "" || org.BillingStatus != "active" {
		t.Fatalf("Personal after cancellation = %#v", org.Billing)
	}
}

func TestSyncRequiresBillingForBusinessWithoutSubscription(t *testing.T) {
	fixture := newBillingFixture(t)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Kind = "business"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	past := activeSubscription("price_pro_monthly")
	past.Status = "past_due"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{past}
	fixture.sync(t)
	if org := fixture.organization(t); org.BillingStatus != "billing_required" || org.Billing.Status != "past_due" {
		t.Fatalf("business past due = %#v", org.Billing)
	}
}

func TestSyncKeepsPersonalPlanDuringDunning(t *testing.T) {
	for _, status := range []string{"past_due", "unpaid", "paused"} {
		t.Run(status, func(t *testing.T) {
			fixture := newBillingFixture(t)
			fixture.sync(t)
			past := activeSubscription("price_pro_monthly")
			past.Status = status
			fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{past}
			fixture.sync(t)

			org := fixture.organization(t)
			if org.PlanID != "plan_pro" || org.Billing.PlanKey != payments.PlanPro || org.Billing.Status != status || org.BillingStatus != "billing_required" {
				t.Fatalf("Personal during %s = %#v", status, org)
			}
		})
	}
}

func TestSyncPicksCurrentSubscriptionAndRemembersTrials(t *testing.T) {
	fixture := newBillingFixture(t)
	old := activeSubscription("price_starter_monthly")
	old.ID, old.Status, old.TrialStarted = "sub_old", "canceled", true
	current := activeSubscription("price_pro_monthly")
	current.TrialStarted = false
	current.CreatedAt = old.CreatedAt.AddDate(0, 0, -1)
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{old, current}
	fixture.sync(t)
	if org := fixture.organization(t); org.Billing.SubscriptionID != "sub_test" || org.PlanID != "plan_pro" || !org.Billing.TrialUsed {
		t.Fatalf("current subscription = %#v", org.Billing)
	}
}

// racingStore saves a competing sync before the first save of a test, as
// when two webhooks for one customer are processed at the same time.
type racingStore struct {
	payments.StateStore
	provider *fakeProvider
	raced    bool
}

func (store *racingStore) Save(ctx context.Context, state payments.CustomerState, version string) error {
	if !store.raced {
		store.raced = true
		if err := store.StateStore.Save(ctx, state, version); err != nil {
			return err
		}
		store.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{activeSubscription("price_business_monthly")}
	}
	return store.StateStore.Save(ctx, state, version)
}

func TestSyncRereadsStripeAfterConcurrentSave(t *testing.T) {
	racing := &racingStore{StateStore: payments.NewMemoryStateStore()}
	fixture := newBillingFixtureWithStore(t, racing)
	racing.provider = fixture.provider
	fixture.sync(t)
	if fixture.provider.fetches != 2 {
		t.Fatalf("Stripe reads = %d, want a re-read after the conflict", fixture.provider.fetches)
	}
	stored, _, _ := racing.Load(t.Context(), "cus_test")
	if stored.PlanKey != payments.PlanBusiness || fixture.organization(t).PlanID != "plan_business" {
		t.Fatalf("stored plan = %q, want the state read after the competing save", stored.PlanKey)
	}
}

func TestUndoSyncsWhenStripeAlreadyReleasedSchedule(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.sync(t)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.ScheduledPlanKey, org.Billing.ScheduledInterval = payments.PlanStarter, payments.IntervalMonthly
		org.Billing.ScheduledAt, org.Billing.ScheduleID = testPeriodEnd, "sub_sched_released"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	fixture.provider.subscription = activeSubscription("price_pro_monthly")
	response := fixture.request(http.MethodPost, "/billing/change/undo")
	org := fixture.organization(t)
	if response.Code != http.StatusSeeOther || fixture.provider.releases != 0 || org.Billing.ScheduledPlanKey != "" || org.Billing.ScheduleID != "" {
		t.Fatalf("undo = %d, releases = %d, billing = %#v", response.Code, fixture.provider.releases, org.Billing)
	}
}

// unavailableStore fails every save as a database outage would.
type unavailableStore struct{ payments.StateStore }

func (unavailableStore) Save(context.Context, payments.CustomerState, string) error {
	return fmt.Errorf("save: %w", core.ErrUnavailable)
}

func TestCheckoutReturnReportsStoreOutageAsUnavailable(t *testing.T) {
	fixture := newBillingFixtureWithStore(t, unavailableStore{payments.NewMemoryStateStore()})
	response := fixture.request(http.MethodGet, "/billing/checkout/return?session_id=cs_test")
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("return during a store outage = %d, want 503", response.Code)
	}
}
