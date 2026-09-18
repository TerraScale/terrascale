package sdk_test

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestGetAuthorizationLease(t *testing.T) {
	const lease = `{"version":3,"tenantId":"tenant-1","epoch":"epoch-9","featureEpoch":"feature-2","audience":"https://api.terrascale.tech","region":"GRU","issuedAt":1700000000000,"validUntil":1900000000000,"keyId":"key-7","signature":"sig"}`
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.Path != "/api/v1/authorization/leases/tenant-1" {
			t.Errorf("lease request = %s %s", r.Method, r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(lease))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	got, err := client.GetAuthorizationLease(context.Background(), "tenant-1")
	if err != nil {
		t.Fatal(err)
	}
	if got.Version != 3 || got.TenantID != "tenant-1" || got.Epoch != "epoch-9" || got.FeatureEpoch != "feature-2" ||
		got.Audience != "https://api.terrascale.tech" || got.Region != "GRU" ||
		got.IssuedAt != 1700000000000 || got.ValidUntil != 1900000000000 || got.KeyID != "key-7" || got.Signature != "sig" {
		t.Fatalf("lease = %+v", got)
	}
}

func TestGetAuthorizationLeaseValidatesBeforeHTTP(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("lease route called with an invalid tenant")
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.GetAuthorizationLease(context.Background(), "  ")
	var validation *sdk.ValidationError
	if !errors.As(err, &validation) {
		t.Fatalf("error = %v, want ValidationError", err)
	}
}
