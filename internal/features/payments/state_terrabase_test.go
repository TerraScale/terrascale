package payments

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strconv"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

// fakeTerraBase serves the document and schema routes the state store uses,
// honouring create-only (If-None-Match: *) and If-Match preconditions.
type fakeTerraBase struct {
	mu        sync.Mutex
	documents map[string][]byte
	revisions map[string]int
	schemas   []string
}

func newFakeTerraBase(t *testing.T) (*terraBaseStateStore, *fakeTerraBase) {
	t.Helper()
	fake := &fakeTerraBase{documents: make(map[string][]byte), revisions: make(map[string]int)}
	mux := http.NewServeMux()
	mux.HandleFunc("PUT /api/v1/databases/{database}/collections/{collection}/schema", func(w http.ResponseWriter, r *http.Request) {
		fake.mu.Lock()
		fake.schemas = append(fake.schemas, r.PathValue("collection"))
		fake.mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"generation":1}`)
	})
	document := "/api/v1/databases/{database}/collections/{collection}/documents/{id}"
	mux.HandleFunc("GET "+document, fake.get)
	mux.HandleFunc("PUT "+document, fake.put)
	server := httptest.NewServer(mux)
	t.Cleanup(server.Close)
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	return newTerraBaseStateStore(client, "dashboard-billing"), fake
}

func (fake *fakeTerraBase) get(w http.ResponseWriter, r *http.Request) {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	key := r.PathValue("collection") + "/" + r.PathValue("id")
	body, ok := fake.documents[key]
	if !ok {
		writeFakeError(w, http.StatusNotFound, "not_found")
		return
	}
	w.Header().Set("ETag", strconv.Quote(strconv.Itoa(fake.revisions[key])))
	w.Header().Set("Content-Type", "application/json")
	w.Write(body)
}

func (fake *fakeTerraBase) put(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(r.Body)
	fake.mu.Lock()
	defer fake.mu.Unlock()
	key := r.PathValue("collection") + "/" + r.PathValue("id")
	_, exists := fake.documents[key]
	current := strconv.Quote(strconv.Itoa(fake.revisions[key]))
	if (r.Header.Get("If-None-Match") == "*" && exists) || (r.Header.Get("If-Match") != "" && (!exists || r.Header.Get("If-Match") != current)) {
		writeFakeError(w, http.StatusPreconditionFailed, "precondition_failed")
		return
	}
	fake.documents[key] = body
	fake.revisions[key]++
	w.Header().Set("ETag", strconv.Quote(strconv.Itoa(fake.revisions[key])))
	w.Header().Set("Content-Type", "application/json")
	io.WriteString(w, `{"revision":"`+strconv.Itoa(fake.revisions[key])+`"}`)
}

func writeFakeError(w http.ResponseWriter, status int, code string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	io.WriteString(w, `{"error":{"code":"`+code+`","message":"`+code+`"}}`)
}

func TestTerraBaseStateStoreDeclaresSchemas(t *testing.T) {
	store, fake := newFakeTerraBase(t)
	if err := store.EnsureSchema(t.Context()); err != nil {
		t.Fatal(err)
	}
	if got := fake.schemas; !reflect.DeepEqual(got, []string{customersCollection, organizationsCollection}) {
		t.Fatalf("schemas = %v", got)
	}
}

func TestTerraBaseStateStoreBindsEachCustomerOnce(t *testing.T) {
	store, _ := newFakeTerraBase(t)
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

func TestTerraBaseStateStoreSavesConditionally(t *testing.T) {
	store, _ := newFakeTerraBase(t)
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
