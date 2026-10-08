package payments

import (
	"errors"
	"net/http"
	"reflect"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
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

func TestTerraBaseStateStoreReadersDistinguishMissingDocumentsFromStorageErrors(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	for _, test := range []struct {
		name string
		code v1.ErrorCode
	}{
		{"missing_document", v1.ErrNotFound},
		{"missing_collection", v1.ErrUnknownCollection},
		{"missing_database", v1.ErrUnknownDatabase},
	} {
		t.Run(test.name, func(t *testing.T) {
			db, key := terrabasetest.Database(t, server, "dashboard")
			switch test.code {
			case v1.ErrNotFound:
				for _, collection := range []string{customersCollection, organizationsCollection} {
					if _, err := db.CreateCollection(t.Context(), collection); err != nil {
						t.Fatal(err)
					}
				}
			case v1.ErrUnknownDatabase:
				client, err := v1.New(v1.Config{BaseURL: server.URL, Token: key, HTTPClient: server.Client})
				if err != nil {
					t.Fatal(err)
				}
				db = client.Database(db.Project(), "missing")
			}
			store := newTerraBaseStateStore(db)
			for _, reader := range []struct {
				name string
				read func(*testing.T) error
			}{
				{"CustomerFor", func(t *testing.T) error {
					customer, found, err := store.CustomerFor(t.Context(), core.NewULID())
					if customer != "" || found {
						t.Errorf("CustomerFor = %q, %v; want empty, false", customer, found)
					}
					return err
				}},
				{"OrganizationFor", func(t *testing.T) error {
					organization, found, err := store.OrganizationFor(t.Context(), "cus_missing")
					if organization != "" || found {
						t.Errorf("OrganizationFor = %q, %v; want empty, false", organization, found)
					}
					return err
				}},
				{"Load", func(t *testing.T) error {
					state, version, err := store.Load(t.Context(), "cus_missing")
					if !reflect.DeepEqual(state, CustomerState{}) || version != "" {
						t.Errorf("Load = %#v, %q; want empty state and version", state, version)
					}
					return err
				}},
			} {
				t.Run(reader.name, func(t *testing.T) {
					err := reader.read(t)
					if test.code == v1.ErrNotFound {
						if err != nil {
							t.Fatalf("missing document = %v; want no error", err)
						}
						return
					}
					if !errors.Is(err, core.ErrUnavailable) || !errors.Is(err, test.code) {
						t.Fatalf("storage error = %v; want ErrUnavailable wrapping %s", err, test.code)
					}
					if apiError, ok := errors.AsType[*v1.APIError](err); !ok || apiError.Status != http.StatusNotFound {
						t.Fatalf("storage error = %v; want the TerraBase HTTP 404", err)
					}
				})
			}
		})
	}
}

func TestTerraBaseStateStoreBindsEachCustomerOnce(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if customer, err := store.BindCustomer(ctx, "7VGX1JD78ZYNTV8K70SZAEK4QV", "cus_a"); err != nil || customer != "cus_a" {
		t.Fatalf("first binding = %q, %v", customer, err)
	}
	if customer, err := store.BindCustomer(ctx, "7VGX1JD78ZYNTV8K70SZAEK4QV", "cus_racing"); err != nil || customer != "cus_a" {
		t.Fatalf("racing binding = %q, %v; want the first customer", customer, err)
	}
	if _, err := store.BindCustomer(ctx, "72RDB9P1FF63DRZKDBD4X8D0YR", "cus_a"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("customer reused by another organization: %v", err)
	}
	if organization, ok, err := store.OrganizationFor(ctx, "cus_a"); err != nil || !ok || organization != "7VGX1JD78ZYNTV8K70SZAEK4QV" {
		t.Fatalf("OrganizationFor = %q, %v, %v", organization, ok, err)
	}
	if _, ok, err := store.CustomerFor(ctx, "3FKZNNRQZQZAZDQZH9B24PHGA1"); err != nil || ok {
		t.Fatalf("unknown organization = %v, %v", ok, err)
	}
}

func TestTerraBaseStateStoreRejectsCustomerReuseAtomically(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if _, err := store.BindCustomer(ctx, "7VGX1JD78ZYNTV8K70SZAEK4QV", "cus_a"); err != nil {
		t.Fatal(err)
	}
	if _, err := store.BindCustomer(ctx, "72RDB9P1FF63DRZKDBD4X8D0YR", "cus_a"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("reuse: %v", err)
	}
	if _, bound, err := store.CustomerFor(ctx, "72RDB9P1FF63DRZKDBD4X8D0YR"); err != nil || bound {
		t.Fatalf("failed binding left organization record: %v %v", bound, err)
	}
}

func TestTerraBaseStateStoreSavesConditionally(t *testing.T) {
	store := realBillingStore(t)
	ctx := t.Context()
	if _, err := store.BindCustomer(ctx, "7VGX1JD78ZYNTV8K70SZAEK4QV", "cus_a"); err != nil {
		t.Fatal(err)
	}
	_, version, err := store.Load(ctx, "cus_a")
	if err != nil || version == "" {
		t.Fatalf("bound customer version = %q, %v", version, err)
	}
	periodEnd := time.Date(2026, time.October, 26, 0, 0, 0, 0, time.UTC)
	state := CustomerState{
		CustomerID: "cus_a", OrganizationID: "7VGX1JD78ZYNTV8K70SZAEK4QV", SubscriptionID: "sub_a", Status: "active",
		PriceID: "price_pro_monthly", PlanKey: PlanPro, Interval: IntervalMonthly,
		CurrentPeriodStart: periodEnd.AddDate(0, -1, 0), CurrentPeriodEnd: periodEnd,
		ScheduleID: "sub_sched_a", ScheduledPlanKey: PlanStarter, ScheduledInterval: IntervalMonthly, TrialUsed: true,
		PaymentMethod: &PaymentMethod{Brand: "visa", Last4: "4242", ExpMonth: 8, ExpYear: 2028},
		Invoices:      []core.Invoice{{ID: "in_a", Status: "paid", AmountCents: 5000, Currency: "usd", CreatedAt: periodEnd.AddDate(0, -1, 0)}},
		SyncedAt:      periodEnd,
		Revision:      (1 << 53) + 1,
	}
	if err := store.Save(ctx, state, version); err != nil {
		t.Fatal(err)
	}
	if err := store.Save(ctx, state, version); !errors.Is(err, ErrStaleState) {
		t.Fatalf("save at a stale version = %v, want ErrStaleState", err)
	}
	if err := store.Save(ctx, CustomerState{CustomerID: "cus_a", OrganizationID: "7VGX1JD78ZYNTV8K70SZAEK4QV", Status: StatusNone}, ""); !errors.Is(err, ErrStaleState) {
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
