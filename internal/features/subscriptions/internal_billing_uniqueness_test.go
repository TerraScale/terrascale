package subscriptions

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"

	sdk "terrascale.tech/sdk/v1"
)

func anotherInternalBillingTarget(t *testing.T, store core.Store, other core.Account, config InternalBillingConfig) InternalBillingConfig {
	t.Helper()
	config.OrganizationID = other.ActiveOrganizationID
	config.SpendCapCents += 500
	config.WriteCentsPerMillion += 700
	config.StorageCentsPerGiBMonth += 900
	if err := store.UpdateOrganization(t.Context(), config.OrganizationID, func(org *core.Organization, _ map[string]core.Plan) error {
		org.Name = "terrascale"
		for _, name := range []string{"ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer"} {
			project := core.Project{
				ID: core.NewID(), OrganizationID: org.ID, Name: name, Status: "active",
				Databases: map[string]core.Database{}, APIKeys: map[string]core.APIKey{},
			}
			org.Projects[project.ID] = project
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return config
}

func seedUnassignedInternalPlan(t *testing.T, store core.Store, principal core.Principal, config InternalBillingConfig) core.Plan {
	t.Helper()
	plan, err := store.Plan(t.Context(), config.BasePlanKey)
	if err != nil {
		t.Fatal(err)
	}
	plan.ID, plan.Key, plan.Name, plan.Visible = core.NewID(), "internal", "TerraScale Internal", false
	if err := store.UpsertPlanAs(t.Context(), principal, plan); err != nil {
		t.Fatal(err)
	}
	return plan
}

type staleInternalBillingPreflight struct {
	core.Store
	afterSnapshot func()
	internalPlan  core.Plan
}

func (s *staleInternalBillingPreflight) PlatformAccounts(ctx context.Context, principal core.Principal) ([]core.Account, error) {
	accounts, err := s.Store.PlatformAccounts(ctx, principal)
	if err == nil {
		s.afterSnapshot()
	}
	return accounts, err
}

func (s *staleInternalBillingPreflight) Plan(ctx context.Context, key string) (core.Plan, error) {
	plan, err := s.Store.Plan(ctx, key)
	if key == "internal" && err == nil {
		s.internalPlan = plan
	}
	return plan, err
}

func TestConfigureInternalBillingRejectsStalePreflightWithReusedPlanID(t *testing.T) {
	for _, existingPlan := range []bool{false, true} {
		t.Run(map[bool]string{false: "first-execution-creates-plan", true: "both-executions-reuse-existing-plan"}[existingPlan], func(t *testing.T) {
			store := memstore.New()
			principal, _, other, first := internalBillingFixture(t, store)
			second := anotherInternalBillingTarget(t, store, other, first)
			if existingPlan {
				seedUnassignedInternalPlan(t, store, principal, first)
			}
			var committed memstore.State
			stale := &staleInternalBillingPreflight{Store: store, afterSnapshot: func() {
				// The loser has already read both eligible organizations before the winner commits.
				if err := ConfigureInternalBilling(t.Context(), store, principal, first); err != nil {
					t.Fatal(err)
				}
				committed = store.Export()
			}}
			if err := ConfigureInternalBilling(t.Context(), stale, principal, second); !errors.Is(err, core.ErrConflict) {
				t.Errorf("stale configuration = %v, want conflict", err)
			}
			firstOrg := committed.Organizations[first.OrganizationID]
			if stale.internalPlan.ID == "" || stale.internalPlan.ID != firstOrg.PlanID {
				t.Fatal("loser did not reuse the winner's internal plan ID")
			}
			if got := store.Export(); !reflect.DeepEqual(got, committed) {
				t.Fatal("loser changed the winner's plan/rates/cap or assigned a second internal organization")
			}
		})
	}
}

type internalBillingTransactionBarrier struct {
	transport              http.RoundTripper
	organizationID, planID string
	ready                  chan bool
	release                chan struct{}
	transactions           atomic.Int32
	conflicts              atomic.Int32
	organizationRetryReads atomic.Int32
}

func (b *internalBillingTransactionBarrier) RoundTrip(request *http.Request) (*http.Response, error) {
	if b.conflicts.Load() > 0 && strings.Contains(request.URL.Path, "/collections/organizations/") {
		b.organizationRetryReads.Add(1)
	}
	target := false
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
		sharedPlan := false
		for _, op := range tx.Ops {
			if op.Op == "put" || op.Op == "create" {
				target = target || op.Collection == "organizations" && op.Document.ID == b.organizationID
				sharedPlan = sharedPlan || op.Collection == "plans" && op.Document.ID == b.planID
			}
		}
		if target && b.transactions.Add(1) == 1 {
			b.ready <- sharedPlan
			select {
			case <-b.release:
			case <-request.Context().Done():
				return nil, request.Context().Err()
			}
		}
	}
	response, err := b.transport.RoundTrip(request)
	if err == nil && target && response.StatusCode == http.StatusPreconditionFailed {
		b.conflicts.Add(1)
	}
	return response, err
}

func TestInternalBillingConcurrentClientsRetryRejectsSecondOrganization(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, server, "internal-billing-uniqueness")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	principal, _, other, first := internalBillingFixture(t, store)
	second := anotherInternalBillingTarget(t, store, other, first)
	sharedPlan := seedUnassignedInternalPlan(t, store, principal, first)
	wantSecond, err := store.Organization(t.Context(), second.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(t.Context(), 30*time.Second)
	defer cancel()
	newClient := func(config InternalBillingConfig) (*terrabase.Store, *internalBillingTransactionBarrier) {
		barrier := &internalBillingTransactionBarrier{
			transport: server.Client.Transport, organizationID: config.OrganizationID, planID: sharedPlan.ID,
			ready: make(chan bool, 1), release: make(chan struct{}),
		}
		httpClient := *server.Client
		httpClient.Transport = barrier
		client, err := sdk.New(sdk.Config{BaseURL: server.URL, Token: key, HTTPClient: &httpClient})
		if err != nil {
			t.Fatal(err)
		}
		return terrabase.New(client.Database(db.Project(), db.Name())), barrier
	}
	firstClient, firstBarrier := newClient(first)
	secondClient, secondBarrier := newClient(second)
	firstResult, secondResult := make(chan error, 1), make(chan error, 1)
	awaitTransaction := func(barrier *internalBillingTransactionBarrier, result <-chan error) {
		select {
		case shared := <-barrier.ready:
			if !shared {
				t.Fatal("organization transaction did not include the reused internal plan ID")
			}
		case err := <-result:
			t.Fatalf("configuration returned before the transaction barrier: %v", err)
		case <-ctx.Done():
			t.Fatal("configuration did not reach the transaction barrier")
		}
	}
	awaitResult := func(result <-chan error) error {
		select {
		case err := <-result:
			return err
		case <-ctx.Done():
			t.Fatal("configuration did not finish after releasing its transaction")
			return ctx.Err()
		}
	}
	go func() { firstResult <- ConfigureInternalBilling(ctx, firstClient, principal, first) }()
	awaitTransaction(firstBarrier, firstResult)
	go func() { secondResult <- ConfigureInternalBilling(ctx, secondClient, principal, second) }()
	awaitTransaction(secondBarrier, secondResult)
	// Both clients have loaded and mutated independent workspaces before either commit reaches TerraBase.
	close(firstBarrier.release)
	if err := awaitResult(firstResult); err != nil {
		t.Fatal(err)
	}
	wantPlan, err := store.Plan(t.Context(), "internal")
	if err != nil {
		t.Fatal(err)
	}
	wantFirst, err := store.Organization(t.Context(), first.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	if wantPlan.ID != sharedPlan.ID || wantPlan.WriteCentsPerMillion != first.WriteCentsPerMillion ||
		wantPlan.StorageCentsPerGiBMonth != first.StorageCentsPerGiBMonth || !wantFirst.Billing.Internal ||
		wantFirst.PlanID != sharedPlan.ID || wantFirst.Billing.SpendCapCents != first.SpendCapCents {
		t.Fatal("first configuration did not commit its shared plan, rates and cap")
	}
	close(secondBarrier.release)
	if err := awaitResult(secondResult); !errors.Is(err, core.ErrConflict) {
		t.Errorf("second configuration = %v, want conflict", err)
	}
	if secondBarrier.conflicts.Load() != 1 || secondBarrier.organizationRetryReads.Load() == 0 {
		t.Errorf("loser did not reload organizations after a real transaction precondition failure: conflicts=%d, reads=%d",
			secondBarrier.conflicts.Load(), secondBarrier.organizationRetryReads.Load())
	}
	if secondBarrier.transactions.Load() != 1 {
		t.Errorf("loser submitted %d transactions; retry must reject before another commit", secondBarrier.transactions.Load())
	}
	assertUnchanged := func() {
		gotPlan, err := store.Plan(t.Context(), "internal")
		if err != nil || gotPlan != wantPlan {
			t.Fatalf("loser changed first plan/rates: %+v, %v", gotPlan, err)
		}
		for _, want := range []core.Organization{wantFirst, wantSecond} {
			got, err := store.Organization(t.Context(), want.ID)
			if err != nil || !reflect.DeepEqual(got, want) {
				t.Fatalf("loser changed organization %s assignment/cap: %+v, %v", want.ID, got.Billing, err)
			}
		}
	}
	assertUnchanged()
	if err := ConfigureInternalBilling(ctx, secondClient, principal, second); !errors.Is(err, core.ErrConflict) {
		t.Errorf("explicit loser retry = %v, want conflict", err)
	}
	assertUnchanged()
}
