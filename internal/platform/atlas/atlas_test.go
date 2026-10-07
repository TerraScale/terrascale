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

const discovery = `{"v":1,"closest":"sfo-1","regions":[
{"id":"sfo-1","name":"San Francisco","country":"US","health":"ok"},
{"id":"gru-1","name":"São Paulo","country":"BR","health":"down"},
{"id":"gru-2","name":"São Paulo 2","country":"BR","health":"ok"},
{"id":"Bad","name":"Bad","country":"XX","health":"ok"}]}`

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
	want := []core.Region{{Code: "GRU", Name: "São Paulo", Country: "br"}, {Code: "SFO", Name: "San Francisco", Country: "us", Healthy: true}}
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
