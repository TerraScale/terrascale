package payments

import (
	"errors"
	"reflect"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"testing"
	"time"
)

func realBillingStore(t *testing.T) *terraBaseStateStore {
	t.Helper()
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, server, "dashboard")
	for _, collection := range []string{customersCollection, organizationsCollection} {
		if _, err := db.CreateCollection(t.Context(), collection); err != nil {
			t.Fatal(err)
		}
	}
	return newTerraBaseStateStore(db)
}

func TestTerraBaseStateStoreBindsEachCustomerOnce(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if customer, err := store.BindCustomer(ctx, "org_a", "cus_a"); err != nil || customer != "cus_a" {
		t.Fatalf("first binding = %q, %v", customer, err)
	}
	if customer, err := store.BindCustomer(ctx, "org_a", "cus_racing"); err != nil || customer != "cus_a" {
		t.Fatalf("racing binding = %q, %v; want the first customer", customer, err)
	}
	if _, err := store.BindCustomer(ctx, "org_b", "cus_a"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("customer reused by another organization: %v", err)
	}
	if organization, ok, err := store.OrganizationFor(ctx, "cus_a"); err != nil || !ok || organization != "org_a" {
		t.Fatalf("OrganizationFor = %q, %v, %v", organization, ok, err)
	}
	if _, ok, err := store.CustomerFor(ctx, "org_unknown"); err != nil || ok {
		t.Fatalf("unknown organization = %v, %v", ok, err)
	}
}

func TestTerraBaseStateStoreRejectsCustomerReuseAtomically(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if _, err := store.BindCustomer(ctx, "org_a", "cus_a"); err != nil {
		t.Fatal(err)
	}
	if _, err := store.BindCustomer(ctx, "org_b", "cus_a"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("reuse: %v", err)
	}
	if _, bound, err := store.CustomerFor(ctx, "org_b"); err != nil || bound {
		t.Fatalf("failed binding left organization record: %v %v", bound, err)
	}
}

func TestTerraBaseStateStoreSavesConditionally(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if _, err := store.BindCustomer(ctx, "org_a", "cus_a"); err != nil {
		t.Fatal(err)
	}
	_, version, err := store.Load(ctx, "cus_a")
	if err != nil || version == "" {
		t.Fatalf("bound customer version = %q, %v", version, err)
	}
	periodEnd := time.Date(2026, time.October, 26, 0, 0, 0, 0, time.UTC)
	state := CustomerState{
		CustomerID: "cus_a", OrganizationID: "org_a", SubscriptionID: "sub_a", Status: "active",
		PriceID: "price_pro_monthly", PlanKey: PlanPro, Interval: IntervalMonthly,
		CurrentPeriodStart: periodEnd.AddDate(0, -1, 0), CurrentPeriodEnd: periodEnd,
		ScheduleID: "sub_sched_a", ScheduledPlanKey: PlanStarter, ScheduledInterval: IntervalMonthly, TrialUsed: true,
		PaymentMethod: &PaymentMethod{Brand: "visa", Last4: "4242", ExpMonth: 8, ExpYear: 2028},
		Invoices:      []core.Invoice{{ID: "in_a", Status: "paid", AmountCents: 5000, Currency: "usd", CreatedAt: periodEnd.AddDate(0, -1, 0)}},
		SyncedAt:      periodEnd,
	}
	if err := store.Save(ctx, state, version); err != nil {
		t.Fatal(err)
	}
	if err := store.Save(ctx, state, version); !errors.Is(err, ErrStaleState) {
		t.Fatalf("save at a stale version = %v, want ErrStaleState", err)
	}
	if err := store.Save(ctx, CustomerState{CustomerID: "cus_a", OrganizationID: "org_a", Status: StatusNone}, ""); !errors.Is(err, ErrStaleState) {
		t.Fatalf("create over an existing state = %v, want ErrStaleState", err)
	}
	loaded, next, err := store.Load(ctx, "cus_a")
	if err != nil || next == version {
		t.Fatalf("reload version = %q (was %q), %v", next, version, err)
	}
	if !reflect.DeepEqual(loaded, state) {
		t.Fatalf("round trip:\n got %#v\nwant %#v", loaded, state)
	}
}
