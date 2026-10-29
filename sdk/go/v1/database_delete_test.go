package v1_test

// Database.Delete against an httptest server: the request it sends and the
// codes it maps.

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestDatabaseDeleteSendsTheDeleteRequest(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodDelete || r.Header.Get("Authorization") != "Bearer "+testToken {
			t.Errorf("request %s, authorization %q", r.Method, r.Header.Get("Authorization"))
		}
		switch r.URL.EscapedPath() {
		case "/v1/projects/" + testProject + "/databases/main":
			w.WriteHeader(http.StatusNoContent)
		case "/v1/projects/" + testProject + "/databases/gone":
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusNotFound)
			io.WriteString(w, `{"error":{"code":"unknown_database","message":"unknown database"}}`)
		default:
			t.Errorf("path %s", r.URL.EscapedPath())
		}
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.Database(testProject, "main").Delete(t.Context()); err != nil {
		t.Fatal(err)
	}
	if err := c.Database(testProject, "gone").Delete(t.Context()); !errors.Is(err, v1.ErrUnknownDatabase) {
		t.Fatalf("missing database: %v", err)
	}
}
