package v1_test

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestDatabaseDeleteIfIDHTTPContract(t *testing.T) {
	t.Parallel()
	for _, status := range []int{http.StatusNoContent, http.StatusConflict, http.StatusNotFound} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			t.Parallel()
			expectedID := v1.NewID()
			var calls atomic.Int64
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != http.MethodDelete || r.URL.EscapedPath() != "/v1/projects/"+testProject+"/databases/main/if-id/"+expectedID || r.URL.RawQuery != "" {
					t.Errorf("unexpected identity-conditioned request: %s %s", r.Method, r.URL)
				}
				if r.Header.Get("Authorization") != "Bearer "+testToken {
					t.Error("missing expected authorization")
				}
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(status)
				switch status {
				case http.StatusConflict:
					io.WriteString(w, `{"error":{"code":"database_identity_mismatch","message":"the database identity does not match expectedId"}}`)
				case http.StatusNotFound:
					io.WriteString(w, `{"error":{"code":"unknown_route","message":"the route does not exist"}}`)
				}
			}))
			t.Cleanup(srv.Close)
			client, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
			if err != nil {
				t.Fatal(err)
			}
			err = client.Database(testProject, "main").DeleteIfID(t.Context(), expectedID)
			switch status {
			case http.StatusNoContent:
				if err != nil {
					t.Fatal(err)
				}
			case http.StatusConflict, http.StatusNotFound:
				code := v1.ErrDatabaseIdentityMismatch
				if status == http.StatusNotFound {
					code = v1.ErrUnknownRoute
				}
				var apiError *v1.APIError
				if !errors.Is(err, code) || !errors.As(err, &apiError) || apiError.Status != status {
					t.Fatalf("unexpected refusal: %v", err)
				}
			}
			if calls.Load() != 1 {
				t.Fatalf("request was retried or fell back: %d calls", calls.Load())
			}
		})
	}
}

func TestDatabaseDeleteIfIDRejectsInvalidSelectorsBeforeHTTP(t *testing.T) {
	t.Parallel()
	var calls atomic.Int64
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusNoContent)
	}))
	t.Cleanup(srv.Close)
	client, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	for _, id := range []string{"", "secret-invalid-id", strings.ToLower(testProject), "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAI"} {
		err := client.Database(testProject, "main").DeleteIfID(t.Context(), id)
		if err == nil || strings.Contains(err.Error(), "secret-invalid-id") {
			t.Fatal("expected safe validation error")
		}
	}
	if err := client.Database("invalid", "main").DeleteIfID(t.Context(), v1.NewID()); err == nil {
		t.Fatal("accepted invalid project")
	}
	if err := client.Database(testProject, "../main").DeleteIfID(t.Context(), v1.NewID()); err == nil {
		t.Fatal("accepted invalid database name")
	}
	if calls.Load() != 0 {
		t.Fatal("invalid selector reached HTTP")
	}
}
