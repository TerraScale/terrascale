package sdk_test

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestTenantsDraftOperations(t *testing.T) {
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		w.Header().Set("Content-Type", "application/json")
		write := func(value any) {
			t.Helper()
			if err := json.NewEncoder(w).Encode(value); err != nil {
				t.Fatalf("encode response: %v", err)
			}
		}

		switch {
		case r.Method == http.MethodPost && r.URL.Path == "/api/v1/tenants":
			if got := r.Header.Get("Idempotency-Key"); got != "create-1" {
				t.Errorf("create idempotency key = %q", got)
			}
			var request sdk.CreateTenantRequest
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
				t.Fatalf("decode create request: %v", err)
			}
			if request.Key != "acme" || request.Template != "standard" || len(request.Regions) != 1 || request.Regions[0] != sdk.RegionGRU {
				t.Errorf("create request = %+v", request)
			}
			write(sdk.Tenant{ID: "tenant-1", Key: request.Key, DatabaseID: "db-1", Template: request.Template, Regions: request.Regions})
		case r.Method == http.MethodGet && r.URL.EscapedPath() == "/api/v1/tenants/acme%2Fwest":
			write(sdk.Tenant{ID: "tenant-2", Key: "acme/west", DatabaseID: "db-2"})
		case r.Method == http.MethodGet && r.URL.Path == "/api/v1/tenants":
			if got := r.URL.Query().Get("limit"); got != "2" {
				t.Errorf("list limit = %q", got)
			}
			if got := r.URL.Query().Get("cursor"); got != "page-1" {
				t.Errorf("list cursor = %q", got)
			}
			write(sdk.TenantPage{
				Tenants:    []sdk.Tenant{{Key: "acme", DatabaseID: "db-1"}},
				NextCursor: "page-2",
			})
		case r.Method == http.MethodDelete && r.URL.Path == "/api/v1/tenants/acme":
			if got := r.Header.Get("X-TerraScale-Delete-Confirmation"); got != "acme" {
				t.Errorf("delete confirmation = %q", got)
			}
			if got := r.Header.Get("Idempotency-Key"); got != "delete-1" {
				t.Errorf("delete idempotency key = %q", got)
			}
			w.WriteHeader(http.StatusNoContent)
		case r.Method == http.MethodPost && r.URL.Path == "/api/v1/tenants/acme/keys":
			if got := r.Header.Get("Idempotency-Key"); got != "key-1" {
				t.Errorf("key idempotency key = %q", got)
			}
			write(sdk.TenantKey{KeyID: "key-1", Token: "secret", TenantKey: "acme"})
		case r.Method == http.MethodPost && r.URL.Path == "/api/v1/tenants/acme/branches":
			if got := r.Header.Get("Idempotency-Key"); got != "branch-1" {
				t.Errorf("branch idempotency key = %q", got)
			}
			write(sdk.TenantBranch{BranchID: "branch-1", TenantKey: "acme", Status: "ready"})
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	ctx := context.Background()

	created, err := client.Tenants.Create(ctx, sdk.CreateTenantRequest{
		Key:      "acme",
		Template: "standard",
		Regions:  []sdk.RegionCode{"gru"},
	}, sdk.WithIdempotencyKey("create-1"))
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	if created.DatabaseID != "db-1" || created.Template != "standard" {
		t.Fatalf("created tenant = %+v", created)
	}

	got, err := client.Tenants.Get(ctx, "acme/west")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got.Key != "acme/west" {
		t.Fatalf("got tenant = %+v", got)
	}

	page, err := client.Tenants.ListPage(ctx, 2, "page-1")
	if err != nil {
		t.Fatalf("ListPage: %v", err)
	}
	if len(page.Tenants) != 1 || page.NextCursor != "page-2" {
		t.Fatalf("tenant page = %+v", page)
	}

	key, err := client.Tenants.CreateKey(ctx, "acme", sdk.WithIdempotencyKey("key-1"))
	if err != nil || key.Token != "secret" || key.TenantKey != "acme" {
		t.Fatalf("CreateKey = %+v, err = %v", key, err)
	}
	branch, err := client.Tenants.CreateBranch(ctx, "acme", sdk.WithIdempotencyKey("branch-1"))
	if err != nil || branch.BranchID != "branch-1" {
		t.Fatalf("CreateBranch = %+v, err = %v", branch, err)
	}
	if err := client.Tenants.Delete(ctx, "acme", sdk.WithIdempotencyKey("delete-1")); err != nil {
		t.Fatalf("Delete: %v", err)
	}

	if requests.Load() != 6 {
		t.Fatalf("request count = %d, want 6", requests.Load())
	}
}

func TestTenantsValidatePlacementAndCancellation(t *testing.T) {
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		http.Error(w, "unexpected request", http.StatusInternalServerError)
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}

	for name, request := range map[string]sdk.CreateTenantRequest{
		"unknown region":   {Key: "acme", Template: "standard", Regions: []sdk.RegionCode{"LHR"}},
		"duplicate region": {Key: "acme", Template: "standard", Regions: []sdk.RegionCode{sdk.RegionGRU, "gru"}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := client.Tenants.Create(context.Background(), request); err == nil {
				t.Fatal("Create succeeded for invalid placement")
			}
		})
	}

	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err = client.Tenants.Get(ctx, "acme")
	if err == nil || !strings.Contains(err.Error(), "context canceled") {
		t.Fatalf("Get canceled error = %v", err)
	}
	if requests.Load() != 0 {
		t.Fatalf("canceled request reached server: %d", requests.Load())
	}
}
