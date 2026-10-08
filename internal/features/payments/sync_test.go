package payments_test

import (
	"context"
	"errors"
	"fmt"
	"math"
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
	if org := fixture.organization(t); org.PlanID != "7CNW2H87JE68ATZX224CV2G625" || org.Billing.Status != "active" {
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
	if org.PlanID != "3BN8V1JQWTB3R3NSZ26SERTHWT" || org.Billing.CheckoutID != "cs_test" {
		t.Fatalf("unconfirmed payment changed the plan or released checkout: %#v", org.Billing)
	}
}

func TestSyncIgnoresOlderStateProjectedLate(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.sync(t)
	fixture.sync(t)
	newer := fixture.organization(t)
	stale := payments.CustomerState{
		CustomerID: "cus_test", OrganizationID: fixture.principal.OrganizationID, Status: payments.StatusNone,
		Revision: newer.Billing.Revision - 1, SyncedAt: newer.Billing.SyncedAt.Add(time.Hour),
	}
	if err := fixture.service.ProjectCustomerState(t.Context(), stale, nil); !errors.Is(err, payments.ErrStaleState) {
		t.Fatalf("older revision with future clock = %v, want ErrStaleState", err)
	}
	if org := fixture.organization(t); !reflect.DeepEqual(org, newer) {
		t.Fatalf("older state replaced newer billing:\n got %#v\nwant %#v", org.Billing, newer.Billing)
	}
	current := stale
	current.Revision = newer.Billing.Revision + 1
	current.SyncedAt = newer.Billing.SyncedAt.Add(-time.Hour)
	if err := fixture.service.ProjectCustomerState(t.Context(), current, nil); err != nil {
		t.Fatal(err)
	}
	org := fixture.organization(t)
	free, err := fixture.store.Plan(t.Context(), "free")
	if err != nil {
		t.Fatal(err)
	}
	if org.Billing.Revision != current.Revision || !org.Billing.SyncedAt.Equal(current.SyncedAt) || org.Billing.Status != payments.StatusNone || org.PlanID != free.ID || org.Billing.SubscriptionID != "" || len(org.Billing.Invoices) != 0 || org.Billing.PaymentMethodLastFour != "" {
		t.Fatalf("higher revision with past clock did not replace billing: %#v", org.Billing)
	}
}

func TestProjectionRejectsZeroRevision(t *testing.T) {
	fixture := newBillingFixture(t)
	before := fixture.organization(t)
	state := payments.CustomerState{CustomerID: "cus_test", OrganizationID: fixture.principal.OrganizationID, Status: payments.StatusNone}
	if err := fixture.service.ProjectCustomerState(t.Context(), state, &fixture.provider.checkout); !errors.Is(err, payments.ErrInvalidBillingInput) {
		t.Fatalf("zero revision = %v, want ErrInvalidBillingInput", err)
	}
	if !reflect.DeepEqual(before, fixture.organization(t)) {
		t.Fatal("zero revision changed organization or checkout")
	}
}

func TestProjectionReappliesEqualRevisionForCheckout(t *testing.T) {
	fixture := newBillingFixture(t)
	subscription := activeSubscription("price_pro_monthly")
	subscription.ScheduleID, subscription.ScheduledPriceID = "sub_sched_test", "price_starter_monthly"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{subscription}
	fixture.provider.checkout.Status = "open"
	fixture.sync(t)
	state, _, err := fixture.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	before := fixture.organization(t)
	if before.Billing.CheckoutID != "cs_test" || before.Billing.ChangeVersion == 0 {
		t.Fatalf("fixture has no open checkout or scheduled change: %#v", before.Billing)
	}
	fixture.provider.checkout.Status = "complete"
	if err := fixture.service.ProjectCustomerState(t.Context(), state, &fixture.provider.checkout); err != nil {
		t.Fatal(err)
	}
	after := fixture.organization(t)
	if after.Billing.Revision != state.Revision || after.Billing.ChangeVersion != before.Billing.ChangeVersion || after.Billing.CheckoutID != "" || after.CheckoutSessions["cs_test"].Status != "complete" {
		t.Fatalf("equal revision did not reconcile checkout idempotently: %#v", after.Billing)
	}
	if err := fixture.service.ProjectCustomerState(t.Context(), state, &fixture.provider.checkout); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(after, fixture.organization(t)) {
		t.Fatal("repeated equal revision changed billing or checkout")
	}
}

func TestSyncReleasesExpiredCheckout(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.provider.customer.Subscriptions = nil
	fixture.provider.checkout.Status, fixture.provider.checkout.SubscriptionID = "expired", ""
	fixture.sync(t)
	org := fixture.organization(t)
	if org.Billing.CheckoutID != "" || org.CheckoutSessions["cs_test"].Status != "canceled" || org.Billing.Status != payments.StatusNone || org.PlanID != "3BN8V1JQWTB3R3NSZ26SERTHWT" {
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
	if org.Billing.ScheduledPlanKey != payments.PlanStarter || org.Billing.ScheduleID != "sub_sched_test" || !org.Billing.ScheduledAt.Equal(testPeriodEnd) || org.PlanID != "7CNW2H87JE68ATZX224CV2G625" {
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
	if org := fixture.organization(t); org.Billing.ScheduledPlanKey != "free" || org.PlanID != "7CNW2H87JE68ATZX224CV2G625" {
		t.Fatalf("scheduled cancellation = %#v", org.Billing)
	}

	canceled := activeSubscription("price_pro_monthly")
	canceled.Status = "canceled"
	fixture.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{canceled}
	fixture.sync(t)
	if org := fixture.organization(t); org.PlanID != "3BN8V1JQWTB3R3NSZ26SERTHWT" || org.Billing.PlanKey != "free" || org.Billing.ScheduledPlanKey != "" || org.BillingStatus != "active" {
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
			if org.PlanID != "7CNW2H87JE68ATZX224CV2G625" || org.Billing.PlanKey != payments.PlanPro || org.Billing.Status != status || org.BillingStatus != "billing_required" {
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
	if org := fixture.organization(t); org.Billing.SubscriptionID != "sub_test" || org.PlanID != "7CNW2H87JE68ATZX224CV2G625" || !org.Billing.TrialUsed {
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
	if stored.Revision != 2 || fixture.organization(t).Billing.Revision != 2 {
		t.Fatalf("revision after one competing save and one successful retry = %d, want 2", stored.Revision)
	}
	if stored.PlanKey != payments.PlanBusiness || fixture.organization(t).PlanID != "09XCWRH05BWWBADSBPTPDMFA7D" {
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

// projectionFlow fails before applying billing, without changing the real service.
type projectionFlow struct {
	payments.BillingFlow
	err             error
	failures, calls int
	provider        *fakeProvider
}

func (flow *projectionFlow) ProjectCustomerState(ctx context.Context, state payments.CustomerState, checkout *payments.CheckoutSession) error {
	flow.calls++
	if flow.calls <= flow.failures {
		if flow.provider != nil {
			flow.provider.customer.Subscriptions = []payments.SubscriptionSnapshot{activeSubscription("price_business_monthly")}
		}
		return flow.err
	}
	return flow.BillingFlow.ProjectCustomerState(ctx, state, checkout)
}

func TestSyncRetriesOnlyStaleProjection(t *testing.T) {
	projectionError := errors.New("projection unavailable")
	for _, test := range []struct {
		name               string
		failures, attempts int
		err, wantErr       error
	}{
		{"retry", 1, 2, payments.ErrStaleState, nil},
		{"exhausted", 3, 3, payments.ErrStaleState, payments.ErrStaleState},
		{"other error", 1, 1, projectionError, projectionError},
	} {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBillingFixture(t)
			before := fixture.organization(t)
			flow := &projectionFlow{BillingFlow: fixture.service, failures: test.failures, err: test.err, provider: fixture.provider}
			state, err := fixture.billing.SyncCustomer(t.Context(), flow, "cus_test")
			if !errors.Is(err, test.wantErr) {
				t.Fatalf("sync = %v, want %v", err, test.wantErr)
			}
			stored, _, loadErr := fixture.state.Load(t.Context(), "cus_test")
			if loadErr != nil || stored.Revision != uint64(test.attempts) || fixture.provider.fetches != test.attempts || flow.calls != test.attempts {
				t.Fatalf("revision = %d, reads = %d, projections = %d, load error = %v; want %d attempts", stored.Revision, fixture.provider.fetches, flow.calls, loadErr, test.attempts)
			}
			if test.wantErr != nil {
				if !reflect.DeepEqual(before, fixture.organization(t)) {
					t.Fatal("failed projection changed organization billing")
				}
			} else {
				plan, err := fixture.store.Plan(t.Context(), payments.PlanBusiness)
				if err != nil {
					t.Fatal(err)
				}
				if state.PlanKey != payments.PlanBusiness || fixture.organization(t).PlanID != plan.ID || fixture.organization(t).Billing.Revision != stored.Revision {
					t.Fatal("projection retry did not refetch and project current Stripe state")
				}
			}
		})
	}
}

type invalidLoadedStore struct {
	payments.StateStore
	mutate func(*payments.CustomerState, *string)
	saves  int
}

func (store *invalidLoadedStore) Load(ctx context.Context, customerID string) (payments.CustomerState, string, error) {
	state, version, err := store.StateStore.Load(ctx, customerID)
	store.mutate(&state, &version)
	return state, version, err
}

func (store *invalidLoadedStore) Save(ctx context.Context, state payments.CustomerState, version string) error {
	store.saves++
	return store.StateStore.Save(ctx, state, version)
}

func TestSyncRejectsInvalidLoadedStateBeforeReadingStripe(t *testing.T) {
	otherOrganization := core.NewULID()
	for _, test := range []struct {
		name   string
		mutate func(*payments.CustomerState, *string)
	}{
		{"missing state", func(state *payments.CustomerState, _ *string) { *state = payments.CustomerState{} }},
		{"missing version", func(_ *payments.CustomerState, version *string) { *version = "" }},
		{"wrong customer", func(state *payments.CustomerState, _ *string) { state.CustomerID = "cus_other" }},
		{"wrong organization", func(state *payments.CustomerState, _ *string) { state.OrganizationID = otherOrganization }},
		{"revision overflow", func(state *payments.CustomerState, _ *string) { state.Revision = math.MaxUint64 }},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := &invalidLoadedStore{StateStore: payments.NewMemoryStateStore(), mutate: test.mutate}
			fixture := newBillingFixtureWithStore(t, store)
			before := fixture.organization(t)
			if _, err := fixture.billing.SyncCustomer(t.Context(), fixture.service, "cus_test"); !errors.Is(err, core.ErrUnavailable) {
				t.Fatalf("invalid loaded state = %v, want ErrUnavailable", err)
			}
			if fixture.provider.fetches != 0 || store.saves != 0 || !reflect.DeepEqual(before, fixture.organization(t)) {
				t.Fatalf("invalid loaded state read Stripe %d times, saved %d times, or changed billing", fixture.provider.fetches, store.saves)
			}
		})
	}
}

func TestMemoryStateStoreInitializesBoundState(t *testing.T) {
	store := payments.NewMemoryStateStore()
	organizationID := core.NewULID()
	if _, err := store.BindCustomer(t.Context(), organizationID, "cus_test"); err != nil {
		t.Fatal(err)
	}
	state, version, err := store.Load(t.Context(), "cus_test")
	want := payments.CustomerState{CustomerID: "cus_test", OrganizationID: organizationID, Status: payments.StatusNone}
	if err != nil || version != "1" || !reflect.DeepEqual(state, want) {
		t.Fatalf("bound state = %#v, version %q, error %v; want %#v at version 1", state, version, err, want)
	}
}
