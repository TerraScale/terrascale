package atlas

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const discovery = `{"v":1,"closest":"us-sanjose-1","regions":[
{"id":"us-sanjose-1","name":"San Francisco","country":"US","api":"https://sfo.api.terrascale.tech","health":"ok"},
{"id":"sa-saopaulo-1","name":"São Paulo","country":"BR","health":"down"},
{"id":"gru-2","name":"São Paulo 2","country":"BR","health":"ok"},
{"id":"xxx-1","name":"Unsupported","country":"XX","health":"ok"},
{"id":"Bad","name":"Bad","country":"XX","health":"ok"}]}`

func TestCodeSupportsOnlyGRUAndSFO(t *testing.T) {
	for _, tc := range []struct{ id, code string }{
		{"sa-saopaulo-1", "GRU"}, {"us-sanjose-1", "SFO"},
		{"gru-1", "GRU"}, {"sfo-1", "SFO"},
		{"xxx-1", ""}, {"", ""}, {"Bad", ""},
	} {
		if got := Code(tc.id); got != tc.code {
			t.Errorf("Code(%q) = %q, want %q", tc.id, got, tc.code)
		}
	}
}

func TestCatalogReadsDiscoveryAndServesTheLastCatalogWhenAtlasFails(t *testing.T) {
	var failing atomic.Bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if failing.Load() || r.URL.Path != "/v1/discovery" {
			http.Error(w, "down", http.StatusServiceUnavailable)
			return
		}
		_, _ = w.Write([]byte(discovery))
	}))
	defer server.Close()
	catalog := &Catalog{BaseURL: server.URL + "/"}
	want := []core.Region{{Code: "GRU", ID: "sa-saopaulo-1", Name: "São Paulo", Country: "br"}, {Code: "SFO", ID: "us-sanjose-1", Name: "San Francisco", Country: "us", API: "https://sfo.api.terrascale.tech", Healthy: true}}
	got, err := catalog.Regions(t.Context())
	if err != nil || len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Fatalf("regions = %+v, %v", got, err)
	}
	failing.Store(true)
	catalog.fetched = time.Time{}
	if got, err := catalog.Regions(t.Context()); err != nil || len(got) != 2 {
		t.Fatalf("stale regions = %+v, %v", got, err)
	}
	if _, err := (&Catalog{BaseURL: server.URL}).Regions(t.Context()); !errors.Is(err, core.ErrDataPlane) {
		t.Fatalf("cold failure = %v", err)
	}
}
