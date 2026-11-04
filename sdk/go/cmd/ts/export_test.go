package main

import (
	"io"
	"net/http"
	"net/http/httptest"

	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func exportTestDB(t *testing.T, handler http.HandlerFunc) *v1.Database {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	c, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main")
}

type exportFailWriter struct{}

func (exportFailWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }
