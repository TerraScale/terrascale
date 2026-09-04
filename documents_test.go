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
