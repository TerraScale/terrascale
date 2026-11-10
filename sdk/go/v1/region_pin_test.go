package v1_test

import (
	"net/http"
	"net/http/httptest"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestRegionPinHeader(t *testing.T) {
	const project = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	var pin string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		pin = r.Header.Get("X-TerraScale-Region-Pin")
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"items":[]}`))
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client(), RegionPin: "sjc"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = c.Databases(t.Context(), project, tb.PageOptions{}); err != nil {
		t.Fatal(err)
	}
	if pin != "sjc" {
		t.Fatalf("X-TerraScale-Region-Pin=%q", pin)
	}
}
