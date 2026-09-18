package sdk_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	sdk "terrascale.tech/sdk"
)

func TestDocumentsMatchTerraBaseWireShapes(t *testing.T) {
	var requests []struct {
		method string
		path   string
		query  url.Values
		body   string
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		requests = append(requests, struct {
			method string
			path   string
			query  url.Values
			body   string
		}{r.Method, r.URL.Path, r.URL.Query(), string(body)})
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == http.MethodPut && strings.HasSuffix(r.URL.Path, "/documents/ada"):
			w.Header().Set("ETag", `"rev-1"`)
			w.Header().Set("X-TerraScale-Session", "session-1")
			_, _ = w.Write([]byte(`{"revision":"rev-1"}`))
		case r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/documents/ada"):
			w.Header().Set("ETag", `"rev-1"`)
			_, _ = w.Write([]byte(`{"name":"Ada"}`))
		case r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/documents"):
			_, _ = w.Write([]byte(`{"documents":[{"id":"ada","version":7,"value":{"name":"Ada"}}]}`))
		case r.Method == http.MethodPatch && strings.HasSuffix(r.URL.Path, "/documents/ada"):
			_, _ = w.Write([]byte(`{"revision":"rev-2"}`))
		case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/documents/batch"):
			_, _ = w.Write([]byte(`{"committed":8,"count":2}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()

	write, err := client.Documents.Put(ctx, "db", "users", "ada", map[string]any{"name": "Ada"}, sdk.WithIdempotencyKey("put-1"))
	if err != nil || write.Revision != "rev-1" {
		t.Fatalf("Put = %+v, err = %v", write, err)
	}
	got, err := client.Documents.Get(ctx, "db", "users", "ada")
	if err != nil || string(got.Body) != `{"name":"Ada"}` {
		t.Fatalf("Get = %+v, err = %v", got, err)
	}
	page, err := client.Documents.List(ctx, "db", "users", 25, "ada")
	if err != nil || len(page.Documents) != 1 || page.Documents[0].Version != 7 || string(page.Documents[0].Value) != `{"name":"Ada"}` {
		t.Fatalf("List = %+v, err = %v", page, err)
	}
	patched, err := client.Documents.Patch(ctx, "db", "users", "ada", map[string]any{"name": "Ada Lovelace"})
	if err != nil || patched.Revision != "rev-2" {
		t.Fatalf("Patch = %+v, err = %v", patched, err)
	}
	batch, err := client.Documents.Batch(ctx, "db", "users", []sdk.BatchOperation{
		{Op: "put", ID: "grace", Value: map[string]any{"name": "Grace"}},
		{Op: "delete", ID: "ada"},
	})
	if err != nil || batch.Count != 2 || batch.Committed != 8 {
		t.Fatalf("Batch = %+v, err = %v", batch, err)
	}

	if len(requests) != 5 {
		t.Fatalf("request count = %d (%+v)", len(requests), requests)
	}
	if requests[0].body != `{"name":"Ada"}` {
		t.Errorf("PUT body = %s; document values must not be wrapped", requests[0].body)
	}
	if requests[2].query.Get("limit") != "25" || requests[2].query.Get("after") != "ada" {
		t.Errorf("list query = %v", requests[2].query)
	}
	if requests[3].body != `{"name":"Ada Lovelace"}` {
		t.Errorf("PATCH body = %s; patch values must not be wrapped", requests[3].body)
	}
	if requests[4].body != `{"operations":[{"op":"put","id":"grace","value":{"name":"Grace"}},{"op":"delete","id":"ada"}]}` {
		t.Errorf("batch body = %s", requests[4].body)
	}
}

func TestDocumentsCreateAndClientBounds(t *testing.T) {
	var requests int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"revision":"rev-1"}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	if _, err := client.Documents.Create(ctx, "db", "users", "one", map[string]any{"x": 1}); err != nil {
		t.Fatalf("Create: %v", err)
	}
	if _, err := client.Documents.Put(ctx, "db", "users", "big", strings.Repeat("x", sdk.MaxDocumentBytes)); err == nil {
		t.Fatal("oversize document unexpectedly succeeded")
	}
	if _, err := client.Documents.List(ctx, "db", "users", sdk.MaxDocumentPageSize+1, ""); err == nil {
		t.Fatal("oversize page unexpectedly succeeded")
	}
	if _, err := client.Documents.Batch(ctx, "db", "users", make([]sdk.BatchOperation, sdk.MaxBatchOperations+1)); err == nil {
		t.Fatal("oversize batch unexpectedly succeeded")
	}
	if requests != 1 {
		t.Fatalf("validation sent %d requests (Create should be the only request)", requests)
	}
}

func TestDocumentErrorsRemainNestedTypedClientErrors(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write([]byte(`{"error":{"code":"document_not_found","message":"missing","fix":"list the collection"}}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Get(context.Background(), "db", "users", "missing")
	var apiErr *sdk.APIError
	if !errors.As(err, &apiErr) || apiErr.Code != "document_not_found" || apiErr.Fix != "list the collection" {
		t.Fatalf("error = %T %+v", err, err)
	}
}

func TestDocumentTypesRetainRawJSONValues(t *testing.T) {
	var row sdk.DocumentRow
	if err := json.Unmarshal([]byte(`{"id":"a","version":3,"value":{"x":1}}`), &row); err != nil {
		t.Fatal(err)
	}
	if row.ID != "a" || row.Version != 3 || string(row.Value) != `{"x":1}` {
		t.Fatalf("row = %+v", row)
	}
}

func BenchmarkDocumentsRoundTrip(b *testing.B) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("ETag", `"18446744073709551615-7"`)
		w.Header().Set("X-TerraScale-Session", "7")
		switch r.Method {
		case http.MethodPut, http.MethodPatch:
			_, _ = w.Write([]byte(`{"revision":"18446744073709551615-8"}`))
		default:
			_, _ = w.Write([]byte(`{"name":"Ada Lovelace","email":"ada@example.com","bio":"first programmer","visits":42,"tags":["math","computing"],"active":true}`))
		}
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		b.Fatal(err)
	}
	document := map[string]any{
		"name": "Ada Lovelace", "email": "ada@example.com", "bio": "first programmer",
		"visits": 42, "tags": []string{"math", "computing"}, "active": true,
	}
	ctx := context.Background()
	b.ReportAllocs()
	b.ResetTimer()
	for b.Loop() {
		if _, err := client.Documents.Put(ctx, "db", "users", "ada", document, sdk.WithIdempotencyKey("bench-put")); err != nil {
			b.Fatal(err)
		}
		got, err := client.Documents.Get(ctx, "db", "users", "ada")
		if err != nil || len(got.Body) == 0 {
			b.Fatalf("get = %+v, err = %v", got, err)
		}
	}
}

func TestDocumentHistoryPageUsesPublishedPagination(t *testing.T) {
	var query url.Values
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		query = r.URL.Query()
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"history":[{"revision":"rev-2","deleted":false}]}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	history, err := client.Documents.HistoryPage(context.Background(), "db", "users", "ada", 25, "rev-1")
	if err != nil || len(history) != 1 || history[0].Revision != "rev-2" {
		t.Fatalf("HistoryPage = %+v, err = %v", history, err)
	}
	if query.Get("limit") != "25" || query.Get("after") != "rev-1" {
		t.Fatalf("history query = %v", query)
	}
}

func TestDocumentPointReadAtQueryAndValidation(t *testing.T) {
	var query url.Values
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		query = r.URL.Query()
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("ETag", `"rev-1"`)
		_, _ = w.Write([]byte(`{"name":"Ada"}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	got, err := client.Documents.Get(t.Context(), "db", "users", "ada", sdk.WithReadAt(1710000000123))
	if err != nil || string(got.Body) != `{"name":"Ada"}` {
		t.Fatalf("Get = %+v, err = %v", got, err)
	}
	if query.Encode() != "at=1710000000123" {
		t.Fatalf("point read query = %q", query.Encode())
	}
	_, err = client.Documents.Get(t.Context(), "db", "users", "ada", sdk.WithReadAt(-1))
	var validationErr *sdk.ValidationError
	if !errors.As(err, &validationErr) || validationErr.Field != "at" {
		t.Fatalf("negative at error = %T %+v", err, err)
	}
	if _, err := client.Documents.List(t.Context(), "db", "users", 0, "", sdk.WithReadAt(1)); err == nil {
		t.Error("list accepted a point in time")
	}
	if requests != 1 {
		t.Fatalf("validation sent %d requests (only the point read should reach HTTP)", requests)
	}
}

func TestConditionalDocumentReadModifyWriteRetriesOriginalCondition(t *testing.T) {
	patches := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodGet {
			w.Header().Set("ETag", `"18446744073709551615-0"`)
			w.Header().Set("X-TerraScale-Session", "7")
			_, _ = w.Write([]byte(`{"count":1}`))
			return
		}
		if r.Header.Get("If-Match") != `"18446744073709551615-0"` || r.Header.Get("Idempotency-Key") != "increment-1" {
			t.Errorf("conditional identity changed: %v", r.Header)
		}
		body, _ := io.ReadAll(r.Body)
		if string(body) != `{"count":2}` {
			t.Errorf("patch body = %s", body)
		}
		patches++
		if patches == 1 {
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"error":{"code":"unavailable"}}`))
			return
		}
		w.Header().Set("ETag", `"18446744073709551615-1"`)
		w.Header().Set("X-TerraScale-Session", "8")
		_, _ = w.Write([]byte(`{"revision":"18446744073709551615-1"}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL, MaxRetryAttempts: 1, RetryDelay: time.Millisecond, SessionToken: "9"})
	if err != nil {
		t.Fatal(err)
	}
	document, err := client.Documents.Get(t.Context(), "db", "counts", "one")
	if err != nil {
		t.Fatal(err)
	}
	if document.ETag != `"18446744073709551615-0"` || document.Session != "7" || client.SessionToken() != "9" {
		t.Fatalf("response metadata = %+v; frontier = %s", document, client.SessionToken())
	}
	written, err := client.Documents.Patch(t.Context(), "db", "counts", "one", map[string]int{"count": 2}, sdk.WithIfMatch(document.ETag), sdk.WithIdempotencyKey("increment-1"))
	if err != nil || written.ETag != `"18446744073709551615-1"` || written.Session != "8" || patches != 2 {
		t.Fatalf("write = %+v, err = %v, attempts = %d", written, err, patches)
	}
}

func TestDocumentConditionsValidateAndDoNotRetryConflicts(t *testing.T) {
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		if r.Header.Get("If-Match") != "*" {
			t.Errorf("If-Match = %q", r.Header.Get("If-Match"))
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusPreconditionFailed)
		_, _ = w.Write([]byte(`{"error":{"code":"precondition_failed"}}`))
	}))
	defer server.Close()
	client, err := sdk.NewClient(sdk.Config{APIBaseURL: server.URL, MaxRetryAttempts: 2, RetryDelay: time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	for _, etag := range []string{"", "revision", `W/"revision"`, `"one", "two"`, "\"bad\nvalue\""} {
		if err := client.Documents.Delete(t.Context(), "db", "users", "one", sdk.WithIfMatch(etag)); err == nil {
			t.Errorf("invalid ETag accepted: %q", etag)
		}
	}
	if _, err := client.Documents.Get(t.Context(), "db", "users", "one", sdk.WithIfMatch("*")); err == nil {
		t.Error("GET accepted document condition")
	}
	if _, err := client.Documents.Create(t.Context(), "db", "users", "one", map[string]int{"x": 1}, sdk.WithIfMatch("*")); err == nil {
		t.Error("create accepted conflicting conditions")
	}
	if _, err := client.Documents.Put(t.Context(), "db", "users", "one", map[string]int{"x": 1}, sdk.WithIfMatch("*"), sdk.WithSchemaGeneration(1)); err == nil {
		t.Error("document condition accepted schema generation")
	}
	if requests != 0 {
		t.Fatalf("validation sent %d requests", requests)
	}
	err = client.Documents.Delete(t.Context(), "db", "users", "one", sdk.WithIfMatch("*"), sdk.WithIdempotencyKey("delete-1"))
	var apiErr *sdk.APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusPreconditionFailed || requests != 1 {
		t.Fatalf("conflict = %v, requests = %d", err, requests)
	}
}
