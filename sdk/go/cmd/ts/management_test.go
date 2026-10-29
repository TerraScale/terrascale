package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

func TestScopedKeyRequest(t *testing.T) {
	t.Parallel()
	for _, scope := range []string{"read", "read-write", ""} {
		t.Run(scope, func(t *testing.T) {
			t.Parallel()
			req, err := scopedKeyRequest([]string{"automation", jsonAuthID}, "30d", "203.0.113.0/24,2001:db8::/32", scope)
			if err != nil {
				t.Fatal(err)
			}
			want := []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDatabasesWrite, tsauth.ScopeDocumentsRead, tsauth.ScopeDocumentsWrite}
			if scope == "read" {
				want = []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDocumentsRead}
			}
			if !slices.Equal(req.Scopes, want) || req.Name != "automation" || req.Database != jsonAuthID || req.ExpiresAt == nil ||
				!slices.Equal(req.AllowedCIDRs, []string{"203.0.113.0/24", "2001:db8::/32"}) {
				t.Fatalf("incorrect key restrictions: %+v", req)
			}
		})
	}
	_, err := scopedKeyRequest([]string{"automation"}, "never", "", "sensitive-invalid-value")
	if !errors.Is(err, errUsage) || strings.Contains(err.Error(), "sensitive-invalid-value") {
		t.Fatalf("invalid scope diagnostic = %v", err)
	}
}

func TestOrganizationInventoryFollowsPagesWithoutProjects(t *testing.T) {
	t.Parallel()
	ids := []string{"01M5N9000000000000000000AA", "01M5N9000000000000000000AB"}
	u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.Path != "/v1/terrascale/organizations" || r.URL.Query().Get("limit") != "100" {
			t.Errorf("unexpected request %s %s", r.Method, r.URL)
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		page := tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{{ID: ids[0], Name: "empty", CreatedBy: ids[0], Role: tsauth.RoleOwner}}, Next: "second"}
		if r.URL.Query().Get("after") == "second" {
			page.Items[0] = tsauth.Organization{ID: ids[1], Name: "empty", CreatedBy: ids[0], Role: tsauth.RoleViewer}
			page.Next = ""
		}
		if err := json.NewEncoder(w).Encode(page); err != nil {
			t.Error(err)
		}
	})
	items, err := organizationInventory(t.Context(), u)
	if err != nil || len(items) != 2 || items[0].ID != ids[0] || items[1].ID != ids[1] {
		t.Fatalf("inventory = %+v, %v", items, err)
	}
}

func TestOrganizationListRejectsIncompleteInventory(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("after") == "" {
			io.WriteString(w, `{"items":[],"next":"second"}`)
			return
		}
		w.WriteHeader(http.StatusForbidden)
		io.WriteString(w, `{"error":{"code":"forbidden_role","message":"membership required"}}`)
	})
	var out bytes.Buffer
	if err := listOrganizations(t.Context(), u, &out); !errors.Is(err, tsauth.ErrForbiddenRole) || out.Len() != 0 {
		t.Fatalf("list error = %v, output = %q", err, out.String())
	}
}

func TestOrganizationListReturnsWriterFailure(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, _ *http.Request) {
		io.WriteString(w, `{"items":[],"next":null}`)
	})
	if err := listOrganizations(t.Context(), u, managementBrokenWriter{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("writer error = %v", err)
	}
}

type managementBrokenWriter struct{}

func (managementBrokenWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func managementUser(t *testing.T, handler http.HandlerFunc) *tsauth.User {
	t.Helper()
	srv := httptest.NewTLSServer(handler)
	t.Cleanup(srv.Close)
	c, err := tsauth.New(tsauth.Config{Issuer: srv.URL + "/terrascale", HTTPClient: srv.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c.WithToken("fixture-sign-in-token")
}

func TestKeyDatabaseScopeRejectedBeforeLogin(t *testing.T) {
	t.Setenv("TERRASCALE_API_KEY", "")
	for _, database := range []string{"orders", "", strings.ToLower(jsonAuthID), "81ARZ3NDEKTSV4RRFFQ69G5FAW"} {
		if err := run(t.Context(), []string{"keys", "create", "ci", database}, io.Discard); !errors.Is(err, errUsage) {
			t.Fatalf("invalid scope must fail before login: %v", err)
		}
	}
}
