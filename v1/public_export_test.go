package v1_test

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const exportRecord = `{"collection":"users","id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","document":{"n":9007199254740993}}` + "\n"

func TestExportStreamsAndResumes(t *testing.T) {
	t.Parallel()
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != http.MethodGet || r.URL.Path != "/v1/projects/"+testProject+"/databases/main/export" || r.Header.Get("Accept") != "application/x-ndjson" || r.Header.Get("Authorization") != "Bearer "+testToken {
			t.Errorf("unexpected request: %s %s", r.Method, r.URL)
		}
		if r.URL.Query().Get("after_collection") != "users" || r.URL.Query().Get("after_id") != "01ARZ3NDEKTSV4RRFFQ69G5FCZ" {
			t.Errorf("resume query: %s", r.URL.RawQuery)
		}
		w.Header().Set("Content-Type", "application/x-ndjson; charset=utf-8")
		fmt.Fprint(w, strings.Repeat(exportRecord, 20)) // total exceeds the per-record limit
	})
	count := 0
	for doc, err := range db.Export(t.Context(), v1.ExportOptions{AfterCollection: "users", AfterID: "01ARZ3NDEKTSV4RRFFQ69G5FCZ"}) {
		if err != nil {
			t.Fatal(err)
		}
		if doc.Collection != "users" || doc.ID != "01ARZ3NDEKTSV4RRFFQ69G5FD0" || string(doc.Document) != `{"n":9007199254740993}` {
			t.Fatalf("document = %+v", doc)
		}
		count++
	}
	if calls != 1 || count != 20 {
		t.Fatalf("calls=%d documents=%d", calls, count)
	}
}

func TestExportRequiresCompleteStream(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, body, media string
		status, rows      int
		wantError         bool
	}{
		{"empty", "", "application/x-ndjson", 200, 0, false},
		{"partial record", exportRecord + `{"collection":`, "application/x-ndjson", 200, 1, true},
		{"missing newline", strings.TrimSuffix(exportRecord, "\n"), "application/x-ndjson", 200, 0, true},
		{"broken HTTP", exportRecord, "application/x-ndjson", 200, 1, true},
		{"malformed", "{\n", "application/x-ndjson", 200, 0, true},
		{"missing document", `{"collection":"users","id":"01ARZ3NDEKTSV4RRFFQ69G5FD0"}` + "\n", "application/x-ndjson", 200, 0, true},
		{"invalid id", strings.Replace(exportRecord, "01ARZ3NDEKTSV4RRFFQ69G5FD0", "bad\\u0001", 1), "application/x-ndjson", 200, 0, true},
		{"oversized", strings.Repeat("x", 2048) + "\n", "application/x-ndjson", 200, 0, true},
		{"wrong media", exportRecord, "application/json", 200, 0, true},
		{"denied", `{"error":{"code":"forbidden_scope","message":"denied"}}`, "application/json", 403, 0, true},
		{"broken error", `{"error":`, "application/json", 403, 0, true},
		{"oversized error", strings.Repeat("x", 2048), "application/json", 403, 0, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			calls := 0
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.Header().Set("Content-Type", tc.media)
				w.Header().Set("X-Request-ID", "export-fixture")
				if tc.name == "broken HTTP" || tc.name == "broken error" {
					w.Header().Set("Content-Length", fmt.Sprint(len(tc.body)+10))
				}
				w.WriteHeader(tc.status)
				fmt.Fprint(w, tc.body)
			})
			rows, failures := 0, 0
			for _, err := range db.Export(t.Context(), v1.ExportOptions{}) {
				if err == nil {
					rows++
					continue
				}
				failures++
				if tc.status == 403 {
					var api *v1.APIError
					if !errors.As(err, &api) || api.Status != 403 || api.RequestID != "export-fixture" {
						t.Fatalf("error = %v", err)
					}
					if len(api.Body) == 0 || len(api.Body) > 1024 || (tc.name != "denied" && api.Unwrap() == nil) {
						t.Fatalf("lost bounded error body or read failure: %+v", api)
					}
				}
				if tc.name == "broken HTTP" && !errors.Is(err, io.ErrUnexpectedEOF) {
					t.Fatalf("error = %v", err)
				}
			}
			if calls != 1 || rows != tc.rows || (failures == 1) != tc.wantError || failures > 1 {
				t.Fatalf("calls=%d rows=%d errors=%d", calls, rows, failures)
			}
		})
	}
}

func TestExportCancellationAndEarlyStop(t *testing.T) {
	t.Parallel()
	for _, cancelStream := range []bool{false, true} {
		t.Run(fmt.Sprint(cancelStream), func(t *testing.T) {
			t.Parallel()
			ctx, cancel := context.WithCancel(t.Context())
			defer cancel()
			closed := make(chan struct{})
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
				defer close(closed)
				w.Header().Set("Content-Type", "application/x-ndjson")
				fmt.Fprint(w, exportRecord)
				w.(http.Flusher).Flush()
				<-r.Context().Done()
			})
			rows, failures := 0, 0
			for _, err := range db.Export(ctx, v1.ExportOptions{}) {
				if err != nil {
					failures++
					if !errors.Is(err, context.Canceled) {
						t.Fatal(err)
					}
					continue
				}
				rows++
				if !cancelStream {
					break
				}
				cancel()
			}
			if rows != 1 || (failures == 1) != cancelStream {
				t.Fatalf("rows=%d errors=%d", rows, failures)
			}
			select {
			case <-closed:
			case <-t.Context().Done():
				t.Fatal("stream body was not closed")
			}
		})
	}
}

func TestExportValidatesResumeBeforeRequest(t *testing.T) {
	t.Parallel()
	db := changesDB(t, func(http.ResponseWriter, *http.Request) { t.Error("unexpected request") })
	for _, opts := range []v1.ExportOptions{{AfterCollection: "users"}, {AfterID: "01ARZ3NDEKTSV4RRFFQ69G5FD0"}, {AfterCollection: "invalid/name", AfterID: "01ARZ3NDEKTSV4RRFFQ69G5FD0"}} {
		failures := 0
		for _, err := range db.Export(t.Context(), opts) {
			if err == nil {
				t.Fatal("invalid resume accepted")
			}
			failures++
		}
		if failures != 1 {
			t.Fatalf("errors=%d", failures)
		}
	}
}

func TestExportRefreshesOnce(t *testing.T) {
	t.Parallel()
	for _, succeeds := range []bool{false, true} {
		t.Run(fmt.Sprint(succeeds), func(t *testing.T) {
			t.Parallel()
			source := &invalidatingSource{}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if succeeds && source.tokens.Load() == 2 {
					w.Header().Set("Content-Type", "application/x-ndjson")
					fmt.Fprint(w, exportRecord)
					return
				}
				w.WriteHeader(http.StatusUnauthorized)
				fmt.Fprint(w, `{"error":{"code":"invalid_key"}}`)
			}))
			t.Cleanup(server.Close)
			c, err := v1.New(v1.Config{BaseURL: server.URL, TokenSource: source})
			if err != nil {
				t.Fatal(err)
			}
			for _, err := range c.Database(testProject, "main").Export(t.Context(), v1.ExportOptions{}) {
				if succeeds && err == nil {
					continue
				}
				var api *v1.APIError
				if !errors.As(err, &api) || api.Status != 401 {
					t.Fatalf("error=%v", err)
				}
			}
			if source.tokens.Load() != 2 || source.invalidations.Load() != 1 {
				t.Fatal("authentication refresh was not bounded to once")
			}
		})
	}
}
