package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestDatabaseBranchIfIDHTTPContract(t *testing.T) {
	t.Parallel()
	const source = "01ARZ3NDEKTSV4RRFFQ69G5FA9"
	const target = "01ARZ3NDEKTSV4RRFFQ69G5FAA"
	for _, status := range []int{201, 404, 409} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != "POST" || r.URL.EscapedPath() != "/proxy%20path/v1/projects/"+testProject+"/databases/main/branches/if-id/"+source || r.URL.RawQuery != "" {
					t.Errorf("unexpected guarded branch request: %s %s", r.Method, r.URL)
				}
				if r.Header.Get("Authorization") != "Bearer "+testToken || r.Header.Get("Content-Type") != "application/json" {
					t.Error("missing standard authorization or JSON content type")
				}
				var body map[string]string
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil || len(body) != 1 || body["name"] != "preview-1" {
					t.Errorf("unexpected name body: %v", err)
				}
				w.Header().Set("Content-Type", "application/json")
				w.Header().Set("X-Request-ID", "request-fixture")
				w.WriteHeader(status)
				switch status {
				case 201:
					io.WriteString(w, `{"id":"`+target+`","projectId":"`+testProject+`","name":"preview-1","branchedFrom":"`+source+`"}`)
				case 409:
					io.WriteString(w, `{"error":{"code":"database_identity_changed","message":"the database identity changed"}}`)
				case 404:
					io.WriteString(w, `{"error":{"code":"unknown_route","message":"unsupported"}}`)
				}
			}))
			t.Cleanup(srv.Close)
			client, err := v1.New(v1.Config{BaseURL: srv.URL + "/proxy%20path", Token: testToken})
			if err != nil {
				t.Fatal(err)
			}
			branch, err := client.Database(testProject, "main").BranchIfID(t.Context(), "preview-1", source)
			if status == 201 {
				if err != nil || branch == nil || branch.ID != target || branch.BranchedFrom == nil || *branch.BranchedFrom != source {
					t.Fatalf("branch=%+v error=%v", branch, err)
				}
			} else {
				code := v1.ErrDatabaseIdentityChanged
				if status == 404 {
					code = v1.ErrUnknownRoute
				}
				var api *v1.APIError
				if branch != nil || !errors.Is(err, code) || !errors.As(err, &api) || api.Status != status || api.RequestID != "request-fixture" {
					t.Fatalf("unexpected refusal: %v", err)
				}
			}
			if calls.Load() != 1 {
				t.Fatalf("replayed or fell back: %d calls", calls.Load())
			}
		})
	}
}

func TestDatabaseBranchIfIDRejectsInvalidSelectors(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		w.WriteHeader(500)
	}))
	t.Cleanup(srv.Close)
	client, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	for _, id := range []string{"", "secret-invalid", strings.ToLower(testProject), "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAI", testProject + "/other", "%2F"} {
		if _, err := client.Database(testProject, "main").BranchIfID(t.Context(), "preview", id); err == nil || strings.Contains(err.Error(), "secret-invalid") {
			t.Fatal("expected safe canonical ID validation error")
		}
	}
	for _, name := range []string{"main", "../preview", "preview/other", "preview?x=y", "preview%2Fother"} {
		if _, err := client.Database(testProject, "main").BranchIfID(t.Context(), name, testProject); err == nil {
			t.Fatal("invalid or equal target accepted")
		}
	}
	for _, db := range []*v1.Database{client.Database("bad", "main"), client.Database(testProject, "main/other")} {
		if _, err := db.BranchIfID(t.Context(), "preview", testProject); err == nil {
			t.Fatal("invalid source selector accepted")
		}
	}
	if calls.Load() != 0 {
		t.Fatal("invalid selector reached HTTP")
	}
}

func TestDatabaseBranchIfIDBoundsErrorBodies(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		w.WriteHeader(409)
		io.WriteString(w, strings.Repeat("x", 64))
	}))
	t.Cleanup(srv.Close)
	client, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	v1.SetResponseLimitForTest(client, 16)
	_, err = client.Database(testProject, "main").BranchIfID(t.Context(), "preview", testProject)
	var api *v1.APIError
	if !errors.As(err, &api) || api.Status != 409 || len(api.Body) != 16 || api.Unwrap() == nil || calls.Load() != 1 {
		t.Fatalf("unbounded error or replay: %v", err)
	}
}
