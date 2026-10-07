package apikeys

import (
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest/dashboardtest"
	"terrascale.tech/sdk/v1"
)

// TestAPIKeysAreTerraBaseDataKeys covers production's API keys page: a key
// it issues reads and writes the project's databases on TerraBase's API,
// the list is TerraBase's without the dashboard's own key, and a revoked
// key stops working.
func TestAPIKeysAreTerraBaseDataKeys(t *testing.T) {
	d := dashboardtest.Start(t)
	ctx := d.WithToken(t.Context())
	service := Service{deps: d.Deps}
	if list, err := service.List(ctx, d.Principal, credentialFilter{}); err != nil || len(list) != 0 {
		t.Fatalf("keys before the project is in TerraBase = %v, %v; want none", list, err)
	}

	issued, err := service.Create(ctx, d.Principal, kindAPIKey, CreateProjectAPIKeyRequest{Name: "checkout", Scopes: []string{"database:read", "database:write"}})
	if err != nil {
		t.Fatal(err)
	}
	_, proj, own := d.TerraBase(t)
	if issued.Secret == "" || own.ID == "" || issued.APIKey.ID == own.ID {
		t.Fatalf("issued = %+v, the dashboard's key %q; want a new TerraBase key", issued, own.ID)
	}
	db := d.API(t, issued.Secret).Database(proj, "orders")
	if _, err := db.Create(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err := db.CreateCollection(ctx, "items"); err != nil {
		t.Fatal(err)
	}
	doc, err := db.Collection("items").Create(ctx, map[string]int{"n": 1})
	if err != nil {
		t.Fatal(err)
	}

	list, err := service.List(ctx, d.Principal, credentialFilter{})
	if err != nil || len(list) != 1 || list[0].ID != issued.APIKey.ID || !list[0].Active {
		t.Fatalf("keys = %+v, %v; want only the issued one", list, err)
	}
	if _, err := service.Revoke(ctx, d.Principal, own.ID, ""); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("revoking the dashboard's own key = %v, want not found", err)
	}
	if _, err := service.Rotate(ctx, d.Principal, issued.APIKey.ID, ""); !errors.Is(err, errLiveCredentials) {
		t.Fatalf("rotate = %v, want unavailable", err)
	}
	if _, err := service.Create(ctx, d.Principal, kindAPIKey, CreateProjectAPIKeyRequest{Name: "admin", Scopes: []string{"*"}}); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("a scope TerraBase lacks = %v, want invalid input", err)
	}
	if _, err := service.Revoke(ctx, d.Principal, issued.APIKey.ID, ""); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Collection("items").Get(ctx, doc.ID); !errors.Is(err, v1.ErrInvalidKey) {
		t.Fatalf("revoked key read = %v, want refused", err)
	}
	// The dashboard's own key keeps working.
	if _, err := d.API(t, own.Secret).Database(proj, "orders").Collection("items").Get(ctx, doc.ID); err != nil {
		t.Fatal(err)
	}
}
