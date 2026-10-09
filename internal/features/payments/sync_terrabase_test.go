package payments_test

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"reflect"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

// Each dashboard instance has its own client and stores, with only the database shared.
func realSyncFixtures(t *testing.T, transport func(http.RoundTripper) http.RoundTripper) (billingFixture, billingFixture) {
	t.Helper()
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, server, "billing-sync")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	fixtures := [2]billingFixture{}
	for i := range fixtures {
		httpClient := *server.Client
		if i == 0 && transport != nil {
			httpClient.Transport = transport(httpClient.Transport)
		}
		client, err := v1.New(v1.Config{BaseURL: server.URL, Token: key, HTTPClient: &httpClient})
		if err != nil {
			t.Fatal(err)
		}
		database := client.Database(db.Project(), db.Name())
		store := terrabase.New(database)
		fixtures[i] = billingFixture{store: store, state: payments.NewTerraBaseStateStoreForTest(database), service: subscriptions.NewBillingService(store)}
	}
	a, b := fixtures[0], fixtures[1]
	if err := a.store.(*terrabase.Store).SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	free, err := a.store.Plan(t.Context(), "free")
	if err != nil {
		t.Fatal(err)
	}
	user := core.User{ID: core.NewULID(), Name: "Owner", Email: "sync@example.test", PasswordHash: "unused", Role: "owner", CreatedAt: testPeriodEnd}
	org := core.Organization{
		ID: core.NewULID(), Name: "Personal", Kind: "personal", Status: "active", OwnerUserID: user.ID,
		PlanID: free.ID, BillingStatus: "active", CreatedAt: testPeriodEnd,
		Members:  map[string]core.Member{user.ID: {UserID: user.ID, Email: user.Email, Name: user.Name, Role: "owner", Status: "active", CreatedAt: testPeriodEnd}},
		Projects: map[string]core.Project{}, Invitations: map[string]core.Invitation{}, CheckoutSessions: map[string]core.CheckoutSession{},
	}
	account := core.Account{ID: core.NewULID(), User: user, Organizations: map[string]core.Organization{org.ID: org}, ActiveOrganizationID: org.ID, CreatedAt: testPeriodEnd}
	if err := a.store.InsertAccount(t.Context(), account); err != nil {
		t.Fatal(err)
	}
	a.principal, b.principal = account.Principal(), account.Principal()
	if _, err := a.state.BindCustomer(t.Context(), org.ID, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if _, err := a.service.RecordCustomer(t.Context(), a.principal, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if err := a.service.RecordExternalCheckout(t.Context(), a.principal, payments.PlanPro, payments.IntervalMonthly, "/usage?tab=billing", payments.CheckoutSession{
		ID: "cs_test", CustomerID: "cus_test", ClientReferenceID: org.ID, ExpiresAt: time.Now().Add(time.Hour),
	}); err != nil {
		t.Fatal(err)
	}
	return a, b
}

// Immutable snapshots and atomic counters avoid the mutable fakeProvider's races.
type syncProvider struct {
	payments.BillingProvider
	first, next payments.StripeCustomer
	checkout    payments.CheckoutSession
	calls       atomic.Int32
	gate        *syncGate
}

func (p *syncProvider) FetchCustomer(ctx context.Context, _ string) (payments.StripeCustomer, error) {
	if p.calls.Add(1) == 1 {
		if p.gate != nil {
			if err := p.gate.pause(ctx); err != nil {
				return payments.StripeCustomer{}, err
			}
		}
		return p.first, nil
	}
	return p.next, nil
}

func (p *syncProvider) RetrieveCheckoutSession(context.Context, string) (payments.CheckoutSession, error) {
	return p.checkout, nil
}

func syncSnapshot(orgID, priceID string) payments.StripeCustomer {
	return payments.StripeCustomer{ID: "cus_test", OrganizationID: orgID, Subscriptions: []payments.SubscriptionSnapshot{activeSubscription(priceID)},
		Invoices: []core.Invoice{{ID: "in_latest", Status: "paid", AmountCents: 5000, Currency: "usd", CreatedAt: testPeriodEnd}},
	}
}

func wireSyncProvider(f *billingFixture, p *syncProvider, flow payments.BillingFlow) {
	p.checkout = payments.CheckoutSession{ID: "cs_test", CustomerID: "cus_test", ClientReferenceID: f.principal.OrganizationID, SubscriptionID: "sub_test", Status: "complete", PaymentStatus: "paid"}
	f.billing = payments.NewStripeForTest(testConfig, p, f.state)
	f.mux = http.NewServeMux()
	payments.Mount(f.mux, core.Deps{Store: f.store, Sessions: fixtureSessions{principal: f.principal}}, flow, f.billing)
}

type syncGate struct {
	ctx              context.Context
	entered, release chan struct{}
	once             sync.Once
}

func newSyncGate(t *testing.T) *syncGate {
	t.Helper()
	ctx, cancel := context.WithTimeout(t.Context(), 30*time.Second)
	gate := &syncGate{ctx: ctx, entered: make(chan struct{}), release: make(chan struct{})}
	t.Cleanup(func() { cancel(); gate.open() })
	return gate
}
func (g *syncGate) open() { g.once.Do(func() { close(g.release) }) }
func (g *syncGate) pause(ctx context.Context) error {
	close(g.entered)
	select {
	case <-g.release:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	case <-g.ctx.Done():
		return g.ctx.Err()
	}
}
func (g *syncGate) wait(t *testing.T) {
	t.Helper()
	select {
	case <-g.entered:
	case <-g.ctx.Done():
		t.Fatal("sync did not reach channel gate: ", g.ctx.Err())
	}
}

func runSyncAsync(t *testing.T, gate *syncGate, run func() error) <-chan error {
	t.Helper()
	result, finished := make(chan error, 1), make(chan struct{})
	go func() { defer close(finished); result <- run() }()
	t.Cleanup(func() {
		gate.open()
		select {
		case <-finished:
		case <-gate.ctx.Done():
		}
	})
	return result
}
func awaitSync(t *testing.T, gate *syncGate, result <-chan error) {
	t.Helper()
	select {
	case err := <-result:
		if err != nil {
			t.Fatal(err)
		}
	case <-gate.ctx.Done():
		t.Fatal("sync did not finish: ", gate.ctx.Err())
	}
}

func assertSyncRevision(t *testing.T, f billingFixture, revision uint64, fetches int32, provider *syncProvider) {
	t.Helper()
	state, _, err := f.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	org := f.organization(t)
	pro, err := f.store.Plan(t.Context(), payments.PlanPro)
	if err != nil {
		t.Fatal(err)
	}
	if state.Revision != revision || org.Billing.Revision != revision || state.PlanKey != payments.PlanPro || org.PlanID != pro.ID {
		t.Fatalf("saved/projected state = %#v / %#v; want Pro revision %d", state, org.Billing, revision)
	}
	if provider.calls.Load() != fetches {
		t.Fatalf("A Stripe reads = %d, want %d", provider.calls.Load(), fetches)
	}
	if org.Billing.CheckoutID != "" || org.CheckoutSessions["cs_test"].Status != "complete" {
		t.Fatalf("checkout not completed: %#v", org.CheckoutSessions)
	}
	if !org.PaymentMethodValidated || org.BillingStatus != "active" || !org.BillingRenewal.Equal(testPeriodEnd) || org.Billing.Status != "active" || org.Billing.Interval != payments.IntervalMonthly || org.Billing.SubscriptionID != "sub_test" {
		t.Fatalf("incomplete subscription entitlements: %#v", org)
	}
	if org.Billing.PaymentMethodLastFour != "4242" || org.Billing.PaymentMethodBrand != "visa" || org.Billing.PaymentMethodExpiry != "08/28" || len(state.Invoices) != 1 || state.Invoices[0].ID != "in_latest" || !reflect.DeepEqual(org.Billing.Invoices, state.Invoices) {
		t.Fatalf("incomplete payment method/invoices: %#v", org.Billing)
	}
	// Customer diagnostics persist whole seconds; ordering is exclusively by revision.
	if org.Billing.SyncedAt.Unix() != state.SyncedAt.Unix() {
		t.Fatalf("projected diagnostic timestamp = %v, saved %v", org.Billing.SyncedAt, state.SyncedAt)
	}
}

func TestTerraBaseSyncRevisionOverridesClockSkew(t *testing.T) {
	a, b := realSyncFixtures(t, nil)
	old, version, err := a.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	old.Revision, old.Status, old.SyncedAt = 1, payments.StatusNone, testPeriodEnd.Add(time.Hour)
	old.PaymentMethod = &payments.PaymentMethod{Brand: "mastercard", Last4: "1111", ExpMonth: 1, ExpYear: 2027}
	old.Invoices = []core.Invoice{{ID: "in_old", Status: "open", AmountCents: 100, Currency: "usd", CreatedAt: testPeriodEnd}}
	if err := a.state.Save(t.Context(), old, version); err != nil {
		t.Fatal(err)
	}
	newer, version, err := b.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	newer.Revision, newer.SyncedAt = 2, testPeriodEnd.Add(-time.Hour)
	newer.Status, newer.SubscriptionID, newer.PlanKey, newer.PriceID, newer.Interval = "active", "sub_test", payments.PlanPro, "price_pro_monthly", payments.IntervalMonthly
	newer.CurrentPeriodEnd, newer.TrialUsed = testPeriodEnd, true
	newer.PaymentMethod = activeSubscription("price_pro_monthly").PaymentMethod
	newer.Invoices = syncSnapshot(b.principal.OrganizationID, "price_pro_monthly").Invoices
	if err := b.state.Save(t.Context(), newer, version); err != nil {
		t.Fatal(err)
	}
	checkout := &payments.CheckoutSession{ID: "cs_test", SubscriptionID: "sub_test", Status: "complete", PaymentStatus: "paid"}
	if err := b.service.ProjectCustomerState(t.Context(), newer, checkout); err != nil {
		t.Fatal(err)
	}
	want := b.organization(t)
	if err := a.service.ProjectCustomerState(t.Context(), old, &payments.CheckoutSession{ID: "cs_test", Status: "expired"}); !errors.Is(err, payments.ErrStaleState) {
		t.Fatalf("old projection = %v, want ErrStaleState", err)
	}
	if err := a.service.ProjectCustomerState(t.Context(), newer, checkout); err != nil {
		t.Fatal(err)
	}
	if got := a.organization(t); !reflect.DeepEqual(got, want) {
		t.Fatalf("stale/equal replay changed organization: got %#v, want %#v", got, want)
	}
	assertSyncRevision(t, a, 2, 0, &syncProvider{})
	persisted, _, err := a.state.Load(t.Context(), "cus_test")
	if err != nil || !reflect.DeepEqual(persisted, newer) {
		t.Fatalf("persistent state = %#v, %v; want %#v", persisted, err, newer)
	}
}

func TestTerraBaseSyncStaleCustomerCASConsumesNoRevision(t *testing.T) {
	a, b := realSyncFixtures(t, nil)
	gate := newSyncGate(t)
	latest := syncSnapshot(a.principal.OrganizationID, "price_pro_monthly")
	provider := &syncProvider{first: syncSnapshot(a.principal.OrganizationID, "price_starter_monthly"), next: latest, gate: gate}
	wireSyncProvider(&a, provider, a.service)
	wireSyncProvider(&b, &syncProvider{first: latest, next: latest}, b.service)
	result := runSyncAsync(t, gate, func() error { _, err := a.billing.SyncCustomer(gate.ctx, a.service, "cus_test"); return err })
	gate.wait(t)
	b.sync(t)
	if got := a.organization(t); got.Billing.Revision != 1 {
		t.Fatalf("B revision = %d, want 1", got.Billing.Revision)
	}
	gate.open()
	awaitSync(t, gate, result)
	assertSyncRevision(t, b, 2, 2, provider)
}

type gatedProjection struct {
	payments.BillingFlow
	gate  *syncGate
	calls atomic.Int32
	stale atomic.Int32
}

func (f *gatedProjection) ProjectCustomerState(ctx context.Context, state payments.CustomerState, checkout *payments.CheckoutSession) error {
	if f.calls.Add(1) == 1 {
		if err := f.gate.pause(ctx); err != nil {
			return err
		}
	}
	err := f.BillingFlow.ProjectCustomerState(ctx, state, checkout)
	if errors.Is(err, payments.ErrStaleState) {
		f.stale.Add(1)
	}
	return err
}

func TestTerraBaseSyncStaleProjectionWebhookRecovers(t *testing.T) {
	a, b := realSyncFixtures(t, nil)
	gate := newSyncGate(t)
	latest := syncSnapshot(a.principal.OrganizationID, "price_pro_monthly")
	provider := &syncProvider{first: syncSnapshot(a.principal.OrganizationID, "price_starter_monthly"), next: latest}
	flow := &gatedProjection{BillingFlow: a.service, gate: gate}
	wireSyncProvider(&a, provider, flow)
	wireSyncProvider(&b, &syncProvider{first: latest, next: latest}, b.service)
	result := runSyncAsync(t, gate, func() error {
		response := a.deliver(t, testEvent{ID: "evt_projection_race", Type: "customer.subscription.updated"}, testWebhookSecret)
		if response.Code != http.StatusOK {
			return fmt.Errorf("signed webhook returned %d, want 200", response.Code)
		}
		return nil
	})
	gate.wait(t)
	saved, _, err := b.state.Load(t.Context(), "cus_test")
	if err != nil || saved.Revision != 1 || b.organization(t).Billing.Revision != 0 {
		t.Fatalf("paused projection state = %#v, %v", saved, err)
	}
	b.sync(t)
	gate.open()
	awaitSync(t, gate, result)
	if flow.calls.Load() != 2 || flow.stale.Load() != 1 {
		t.Fatalf("projection calls/stale = %d/%d, want 2/1", flow.calls.Load(), flow.stale.Load())
	}
	assertSyncRevision(t, b, 3, 2, provider)
}

// Gate only A's first armed transaction; the SDK still sends it to real TerraBase.
type syncTransactionGate struct {
	http.RoundTripper
	gate     *syncGate
	armed    atomic.Bool
	rejected atomic.Bool
}

func (g *syncTransactionGate) RoundTrip(request *http.Request) (*http.Response, error) {
	paused := request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/transactions") && g.armed.CompareAndSwap(true, false)
	if paused {
		if err := g.gate.pause(request.Context()); err != nil {
			return nil, err
		}
	}
	response, err := g.RoundTripper.RoundTrip(request)
	if paused && err == nil && response.StatusCode >= 400 {
		body, readErr := io.ReadAll(response.Body)
		response.Body.Close()
		if readErr != nil {
			return nil, readErr
		}
		response.Body = io.NopCloser(bytes.NewReader(body))
		var failure struct {
			Error struct {
				Code v1.ErrorCode `json:"code"`
			} `json:"error"`
		}
		if json.Unmarshal(body, &failure) == nil && failure.Error.Code == v1.ErrPreconditionFailed {
			g.rejected.Store(true)
		}
	}
	return response, err
}

func TestTerraBaseSyncOrganizationTransactionReloadsRevision(t *testing.T) {
	gate := newSyncGate(t)
	transport := &syncTransactionGate{gate: gate}
	a, b := realSyncFixtures(t, func(base http.RoundTripper) http.RoundTripper { transport.RoundTripper = base; return transport })
	old, version, err := a.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	old.Revision, old.Status, old.SyncedAt = 1, payments.StatusNone, testPeriodEnd.Add(time.Hour)
	if err := a.state.Save(t.Context(), old, version); err != nil {
		t.Fatal(err)
	}
	newer, version, err := b.state.Load(t.Context(), "cus_test")
	if err != nil {
		t.Fatal(err)
	}
	newer.Revision, newer.Status, newer.PlanKey, newer.Interval, newer.SubscriptionID = 2, "active", payments.PlanPro, payments.IntervalMonthly, "sub_test"
	newer.SyncedAt, newer.CurrentPeriodEnd = testPeriodEnd.Add(-time.Hour), testPeriodEnd
	newer.PaymentMethod, newer.Invoices = activeSubscription("price_pro_monthly").PaymentMethod, syncSnapshot(a.principal.OrganizationID, "price_pro_monthly").Invoices
	if err := b.state.Save(t.Context(), newer, version); err != nil {
		t.Fatal(err)
	}
	transport.armed.Store(true)
	result := runSyncAsync(t, gate, func() error {
		err := a.service.ProjectCustomerState(gate.ctx, old, nil)
		if !errors.Is(err, payments.ErrStaleState) {
			return errors.New("transaction retry did not reject revision 1 as stale")
		}
		return nil
	})
	gate.wait(t)
	if err := b.service.ProjectCustomerState(t.Context(), newer, &payments.CheckoutSession{ID: "cs_test", Status: "complete", SubscriptionID: "sub_test"}); err != nil {
		t.Fatal(err)
	}
	want := b.organization(t)
	gate.open()
	awaitSync(t, gate, result)
	if !transport.rejected.Load() {
		t.Fatal("A transaction was not rejected by real TerraBase")
	}
	if got := a.organization(t); !reflect.DeepEqual(got, want) {
		t.Fatalf("transaction retry overwrote newer organization: got %#v, want %#v", got, want)
	}
	assertSyncRevision(t, a, 2, 0, &syncProvider{})
}
