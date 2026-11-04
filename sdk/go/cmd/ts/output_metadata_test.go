package main

import (
	"net/http"
	"net/http/httptest"

	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func jsonDatabaseSession(t *testing.T, handler http.HandlerFunc) *session {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	client, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return &session{tb: client, project: jsonAuthID}
}
