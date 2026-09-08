package sdk

import (
	"context"
	"errors"
	"math"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestOperationsListUsesProjectScopeAndOpaquePagination(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.Path != "/api/v1/operations" {
			t.Fatalf("request = %s %s", r.Method, r.URL)
		}
		if got := r.Header.Get("X-TerraScale-Project-ID"); got != "project-1" {
			t.Fatalf("project header = %q", got)
		}
		if got := r.URL.Query().Get("after"); got != "opaque/cursor" {
			t.Fatalf("after = %q", got)
		}
		if got := r.URL.Query().Get("limit"); got != "2" {
			t.Fatalf("limit = %q", got)
		}
		_, _ = w.Write([]byte(`{"operations":[{"operation_id":"op-1","kind":"database_create","database_id":"db-1","name":"demo","state":"running","stage":"keys_provisioned","created_at":"2026-09-06T12:00:00Z","revision":18446744073709551615,"cancellable":false,"cancellation_reason":"Provisioning has no safe cancellation action"}],"next_cursor":"next/cursor","covered_kinds":["database_create","branch_create"]}`))
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, ProjectID: "project-1"})
	if err != nil {
		t.Fatal(err)
	}
	page, err := client.Operations.List(t.Context(), "opaque/cursor", 2)
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Operations) != 1 || page.Operations[0].Revision == nil || *page.Operations[0].Revision != math.MaxUint64 || page.Operations[0].CreatedAt == nil || page.Operations[0].CreatedAt.IsZero() {
		t.Fatalf("operation facts lost: %+v", page)
	}
	if page.NextCursor == nil || *page.NextCursor != "next/cursor" {
		t.Fatalf("next cursor = %v", page.NextCursor)
	}
}

func TestOperationsListPreservesAbsentOptionalFacts(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"operations":[{"operation_id":"op-2","kind":"branch_create","database_id":"db-2","name":"branch","state":"queued","stage":"accepted","cancellable":false,"cancellation_reason":"not cancellable"}],"covered_kinds":["branch_create"]}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, ProjectID: "project-1"})
	if err != nil {
		t.Fatal(err)
	}
	page, err := client.Operations.List(t.Context(), "", 1)
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Operations) != 1 || page.Operations[0].CreatedAt != nil || page.Operations[0].Revision != nil {
		t.Fatalf("absent facts were invented: %+v", page.Operations[0])
	}
}

func TestOperationsListRejectsMissingCoverage(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"operations":[]}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, ProjectID: "project-1"})
	if err != nil {
		t.Fatal(err)
	}
	var validation *ValidationError
	if _, err := client.Operations.List(t.Context(), "", 1); !errors.As(err, &validation) || validation.Field != "covered_kinds" {
		t.Fatalf("missing coverage error = %T: %v", err, err)
	}
}

func TestOperationsListValidationAndCancellation(t *testing.T) {
	client, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1", ProjectID: "project-1"})
	if err != nil {
		t.Fatal(err)
	}
	var validation *ValidationError
	if _, err := client.Operations.List(t.Context(), "", 51); !errors.As(err, &validation) {
		t.Fatalf("invalid limit error = %T: %v", err, err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := client.Operations.List(ctx, "", 1); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled request error = %v", err)
	}
	clientNoProject, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := clientNoProject.Operations.List(t.Context(), "", 1); !errors.As(err, &validation) {
		t.Fatalf("missing project error = %T: %v", err, err)
	}
}
