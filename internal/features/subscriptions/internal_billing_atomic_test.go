package subscriptions

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	sdk "terrascale.tech/sdk/v1"
)

type rejectInternalOrganizationWrite struct {
	transport              http.RoundTripper
	organizationID, planID string
	rejected               atomic.Int32
	planInRejected         atomic.Bool
}

func (r *rejectInternalOrganizationWrite) RoundTrip(request *http.Request) (*http.Response, error) {
	if request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/transactions") {
		body, err := io.ReadAll(request.Body)
		if err != nil {
			return nil, err
		}
		request.Body.Close()
		request.Body = io.NopCloser(bytes.NewReader(body))
		var tx struct {
			Ops []struct {
				Op, Collection string
				Document       struct{ ID string }
			}
		}
		if err := json.Unmarshal(body, &tx); err != nil {
			return nil, err
		}
		organization, plan := false, false
		for _, op := range tx.Ops {
			if op.Op != "put" && op.Op != "create" {
				continue
			}
			organization = organization || op.Collection == "organizations" && op.Document.ID == r.organizationID
			plan = plan || op.Collection == "plans" && op.Document.ID == r.planID
		}
		if organization {
			r.rejected.Add(1)
			r.planInRejected.Store(plan)
			return nil, errors.New("injected organization transaction failure")
		}
	}
	return r.transport.RoundTrip(request)
}

func TestInternalBillingFailedReconfigurationPreservesActivePolicy(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, server, "internal-billing-atomic")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	principal, _, _, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
		t.Fatal(err)
	}
	wantPlan, err := store.Plan(t.Context(), "internal")
	if err != nil {
		t.Fatal(err)
	}
	wantOrg, err := store.Organization(t.Context(), config.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	base, err := store.Plan(t.Context(), config.BasePlanKey)
	if err != nil {
		t.Fatal(err)
	}
	base.PriceCents += 123
	base.StorageMiB += 100
	base.OperationsPerMonth += 100
	if err := store.UpsertPlanAs(t.Context(), principal, base); err != nil {
		t.Fatal(err)
	}
	config.SpendCapCents += 500
	config.WriteCentsPerMillion += 7
	config.StorageCentsPerGiBMonth += 9
	transport := server.Client.Transport
	if transport == nil {
		transport = http.DefaultTransport
	}
	reject := &rejectInternalOrganizationWrite{transport: transport, organizationID: config.OrganizationID, planID: wantPlan.ID}
	httpClient := *server.Client
	httpClient.Transport = reject
	client, err := sdk.New(sdk.Config{BaseURL: server.URL, Token: key, HTTPClient: &httpClient})
	if err != nil {
		t.Fatal(err)
	}
	failing := terrabase.New(client.Database(db.Project(), db.Name()))
	if err := ConfigureInternalBilling(t.Context(), failing, principal, config); !errors.Is(err, core.ErrUnavailable) {
		t.Fatalf("reconfiguration = %v, want unavailable", err)
	}
	if reject.rejected.Load() == 0 || !reject.planInRejected.Load() {
		t.Fatal("plan and organization were not submitted in the same failed transaction")
	}
	gotPlan, err := store.Plan(t.Context(), "internal")
	if err != nil || gotPlan != wantPlan {
		t.Fatalf("failed commit changed active prices/rates/allowances: %+v, %v", gotPlan, err)
	}
	gotOrg, err := store.Organization(t.Context(), config.OrganizationID)
	if err != nil || !reflect.DeepEqual(gotOrg, wantOrg) {
		t.Fatalf("failed commit changed active assignment/cap: %+v, %v", gotOrg.Billing, err)
	}
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
		t.Fatal(err)
	}
	gotPlan, err = store.Plan(t.Context(), "internal")
	if err != nil || gotPlan.ID != wantPlan.ID || gotPlan.PriceCents != base.PriceCents || gotPlan.WriteCentsPerMillion != config.WriteCentsPerMillion {
		t.Fatalf("successful retry did not replace the plan: %+v, %v", gotPlan, err)
	}
	gotOrg, err = store.Organization(t.Context(), config.OrganizationID)
	if err != nil || gotOrg.PlanID != gotPlan.ID || gotOrg.Billing.SpendCapCents != config.SpendCapCents {
		t.Fatalf("successful retry did not replace the cap: %+v, %v", gotOrg.Billing, err)
	}
}
