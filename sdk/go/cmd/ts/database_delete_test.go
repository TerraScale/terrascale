package main

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"sync"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestDatabaseDeleteSendsOnlyDelete(t *testing.T) {
	t.Parallel()
	const project = "01M5N8CG630T6P449ES15GNZ8N"
	for _, status := range []int{http.StatusNoContent, http.StatusNotFound, http.StatusForbidden, http.StatusMisdirectedRequest, http.StatusServiceUnavailable} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			t.Parallel()
			var mu sync.Mutex
			var calls []string
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				mu.Lock()
				calls = append(calls, r.Method+" "+r.URL.Path)
				mu.Unlock()
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(status)
				if status != http.StatusNoContent {
					io.WriteString(w, `{"error":{"code":"test","message":"test"}}`)
				}
			}))
			defer server.Close()
			client, err := v1.New(v1.Config{BaseURL: server.URL, Token: "header.project.signature"})
			if err != nil {
				t.Fatal(err)
			}
			s := session{tb: client, project: project}
			err = s.run(t.Context(), "db delete", []string{"app"}, io.Discard)
			var apiErr *v1.APIError
			if (status == http.StatusNoContent) != (err == nil) || (err != nil && (!errors.As(err, &apiErr) || apiErr.Status != status)) {
				t.Fatalf("status %d: err = %v", status, err)
			}
			mu.Lock()
			defer mu.Unlock()
			// A failed DELETE must leave placement untouched: no PATCH, ever.
			if want := []string{"DELETE /v1/projects/" + project + "/databases/app"}; !slices.Equal(calls, want) {
				t.Fatalf("requests = %q, want %q", calls, want)
			}
		})
	}
}
