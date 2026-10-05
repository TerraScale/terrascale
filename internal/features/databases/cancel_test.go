package databases

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/v1"
)

// cancelingStore honors ctx like a database store and cancels the request
// right before its cancelAt-th UpdateAs, as a client disconnect would.
type cancelingStore struct {
	core.Store
	cancel   context.CancelFunc
	calls    int
	cancelAt int
}

func (s *cancelingStore) UpdateAs(ctx context.Context, p core.Principal, mutate func(*core.Account) error) error {
	if s.calls++; s.calls == s.cancelAt {
		s.cancel()
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	return s.Store.UpdateAs(ctx, p, mutate)
}

type testSDK struct{ url string }

func (f testSDK) ForDocuments(_, _ string) (*v1.Client, error) {
	return v1.New(v1.Config{BaseURL: f.url, Token: "tb_" + strings.Repeat("a", 43)})
}

func (f testSDK) ForScope(tenantID, projectID string) (*sdk.Client, error) {
	return sdk.NewClient(sdk.Config{APIBaseURL: f.url, AccessToken: "token", TenantID: tenantID, ProjectID: projectID})
}

func cancelFixture(t *testing.T, seed core.Database) (*memstore.Store, core.Principal) {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "cancel-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if seed.ID == "" {
		return store, p
	}
	if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.PlanID = "plan_starter" // Free allows one database, a branch needs two.
		project := org.Projects[p.ProjectID]
		seed.ProjectID = project.ID
		project.Databases[seed.ID] = seed
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return store, p
}

func storedDatabases(t *testing.T, store *memstore.Store, p core.Principal) map[string]core.Database {
	t.Helper()
	snapshot, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	return snapshot.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases
}

func TestCanceledProvisioningStillRecordsTheOutcome(t *testing.T) {
	for _, tc := range []struct {
		name string
		seed core.Database
		run  func(Service, context.Context, core.Principal) error
	}{
		{"create", core.Database{}, func(s Service, ctx context.Context, p core.Principal) error {
			_, err := s.Create(ctx, p, "orders")
			return err
		}},
		{"branch", core.Database{ID: "db_src", Name: "src", Region: "GRU", Regions: []string{"GRU"}, Status: "ready"}, func(s Service, ctx context.Context, p core.Principal) error {
			_, err := s.Branch(ctx, p, "db_src", "orders")
			return err
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store, p := cancelFixture(t, tc.seed)
			ctx, cancel := context.WithCancel(t.Context())
			defer cancel()
			// Without an SDK the data plane fails right after the record is
			// committed, and the request is canceled before "failed" is saved.
			s := Service{deps: core.Deps{Store: &cancelingStore{Store: store, cancel: cancel, cancelAt: 2}}}
			if err := tc.run(s, ctx, p); err == nil {
				t.Fatal("provisioning without a data plane succeeded")
			}
			status := "missing"
			for _, db := range storedDatabases(t, store, p) {
				if db.Name == "orders" {
					status = db.Status
				}
			}
			if status != "failed" {
				t.Fatalf("status = %q, want failed", status)
			}
		})
	}
}

func TestCanceledDeleteStillRemovesTheRecord(t *testing.T) {
	store, p := cancelFixture(t, core.Database{ID: "db_tmp", Name: "tmp", Region: "GRU", Status: "ready"})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodDelete || r.URL.Path != "/api/v1/databases/tmp" {
			http.NotFound(w, r)
			return
		}
		request := sdk.DeletionRequest{Environment: "test", TenantID: p.OrganizationID, ProjectID: p.ProjectID, DatabaseID: "tmp", OperationID: r.Header.Get("Idempotency-Key")}
		stop := sdk.DeletionStop{DeletionIdentity: sdk.DeletionIdentity{Environment: "test", OrganizationID: p.OrganizationID, ProjectID: p.ProjectID, DatabaseID: "tmp"}, OperationID: request.OperationID, Region: "GRU"}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusAccepted)
		_ = json.NewEncoder(w).Encode(sdk.DeletionOperation{RequestedAt: time.Now(), Request: request, State: "preparing", Targets: []sdk.DeletionTarget{{Stop: stop}}})
	}))
	defer server.Close()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	// The provider deletes the database, then the request is canceled
	// before the record is removed.
	s := Service{deps: core.Deps{Store: &cancelingStore{Store: store, cancel: cancel, cancelAt: 1}, SDK: testSDK{server.URL}}}
	if err := s.Delete(ctx, p, "db_tmp", "tmp"); err != nil {
		t.Fatal(err)
	}
	if _, ok := storedDatabases(t, store, p)["db_tmp"]; ok {
		t.Fatal("the provider deleted the database but its record is still stored")
	}
}
