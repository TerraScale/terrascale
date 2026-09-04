package sdk_test

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestTenantsFailClosedUntilTerraBasePublishesRoutes(t *testing.T) {
	var requests int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		http.Error(w, "unexpected tenant request", http.StatusInternalServerError)
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	ctx := context.Background()

	var gap *sdk.ContractGapError
	if _, err := client.Tenants.Create(ctx, sdk.CreateTenantRequest{Key: "acme", Regions: []sdk.RegionCode{sdk.RegionGRU}}, sdk.WithIdempotencyKey("create-1")); !errors.As(err, &gap) {
		t.Fatalf("Create error = %T: %v, want ContractGapError", err, err)
	}
	if gap.Operation != "tenants.create" || gap.Method != http.MethodPost || gap.Path != "/api/v1/tenants" {
		t.Fatalf("Create gap metadata = %+v", gap)
	}
	if _, err := client.Tenants.Get(ctx, "acme"); !errors.As(err, &gap) {
		t.Fatalf("Get error = %T: %v, want ContractGapError", err, err)
	}
	if _, err := client.Tenants.ListPage(ctx, 25, ""); !errors.As(err, &gap) {
		t.Fatalf("List error = %T: %v, want ContractGapError", err, err)
	}
	if _, err := client.Tenants.CreateKey(ctx, "acme", sdk.WithIdempotencyKey("key-1")); !errors.As(err, &gap) {
		t.Fatalf("CreateKey error = %T: %v, want ContractGapError", err, err)
	}
	if _, err := client.Tenants.CreateBranch(ctx, "acme", sdk.WithIdempotencyKey("branch-1")); !errors.As(err, &gap) {
		t.Fatalf("CreateBranch error = %T: %v, want ContractGapError", err, err)
	}
	if err := client.Tenants.Delete(ctx, "acme", sdk.WithIdempotencyKey("delete-1"), sdk.WithDeleteConfirmation("acme")); !errors.As(err, &gap) {
		t.Fatalf("Delete error = %T: %v, want ContractGapError", err, err)
	}
	if requests != 0 {
		t.Fatalf("contract-gap operations reached server: %d", requests)
	}
}

func TestTenantsDeleteRequiresConfirmationAndIdempotency(t *testing.T) {
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	var idem *sdk.IdempotencyError
	if err := client.Tenants.Delete(context.Background(), "acme", sdk.WithDeleteConfirmation("acme")); !errors.As(err, &idem) {
		t.Fatalf("missing idempotency error = %T: %v", err, err)
	}
	var confirmation *sdk.DeleteConfirmationError
	if err := client.Tenants.Delete(context.Background(), "acme", sdk.WithIdempotencyKey("delete-1")); !errors.As(err, &confirmation) {
		t.Fatalf("missing confirmation error = %T: %v", err, err)
	}
}

func TestTenantsValidatePlacementAndPagination(t *testing.T) {
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	for name, request := range map[string]sdk.CreateTenantRequest{
		"unknown region":   {Key: "acme", Regions: []sdk.RegionCode{"LHR"}},
		"duplicate region": {Key: "acme", Regions: []sdk.RegionCode{sdk.RegionGRU, "gru"}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := client.Tenants.Create(context.Background(), request, sdk.WithIdempotencyKey("create-1")); err == nil {
				t.Fatal("Create succeeded for invalid placement")
			}
		})
	}
	var pageErr *sdk.PaginationError
	if _, err := client.Tenants.ListPage(context.Background(), -1, ""); !errors.As(err, &pageErr) {
		t.Fatalf("negative limit error = %T: %v", err, err)
	}
	if _, err := client.Tenants.ListPage(context.Background(), 1001, ""); !errors.As(err, &pageErr) {
		t.Fatalf("oversized limit error = %T: %v", err, err)
	}
	var validation *sdk.ValidationError
	if _, err := client.Tenants.Get(context.Background(), "../private"); !errors.As(err, &validation) {
		t.Fatalf("unsafe tenant key error = %T: %v", err, err)
	}
}
