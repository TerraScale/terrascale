package app

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

type observedTransport struct {
	base    http.RoundTripper
	mu      sync.Mutex
	methods []string
}

func (o *observedTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	o.mu.Lock()
	o.methods = append(o.methods, r.Method+" "+r.URL.Path)
	o.mu.Unlock()
	return o.base.RoundTrip(r)
}

func TestTerraBaseStartupRecreatesBootstrapAndReadinessNeverWrites(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	observed := &observedTransport{base: tb.Client.Transport}
	cfg := config.Config{
		TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		TerraBaseHTTPClient: &http.Client{Transport: observed}, CSRFSecret: []byte(strings.Repeat("r", 32)),
	}
	application, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	check := func(want int) {
		t.Helper()
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/health/ready", nil))
		if response.Code != want || !strings.Contains(response.Body.String(), `"backend":"terrabase"`) {
			t.Fatalf("readiness = %d %s", response.Code, response.Body.String())
		}
	}
	collections, err := db.Collections(t.Context(), v1.ListOptions{Limit: 200})
	if err != nil || len(collections.Items) == 0 {
		t.Fatalf("startup did not recreate collections: %v %v", collections, err)
	}
	plan, err := terrabase.New(db).Plan(t.Context(), "free")
	if err != nil || !core.ValidID(plan.ID) {
		t.Fatalf("startup did not recreate a ULID plan catalog: %+v %v", plan, err)
	}
	if _, err := New(cfg); err != nil {
		t.Fatal(err)
	}
	again, err := terrabase.New(db).Plan(t.Context(), "free")
	if err != nil || again.ID != plan.ID {
		t.Fatalf("repeated startup changed plan identity: %+v %v", again, err)
	}
	observed.mu.Lock()
	observed.methods = nil
	observed.mu.Unlock()
	check(http.StatusOK)
	check(http.StatusOK)
	observed.mu.Lock()
	defer observed.mu.Unlock()
	if len(observed.methods) != 2 {
		t.Fatalf("startup/health requests = %v", observed.methods)
	}
	for _, request := range observed.methods {
		if !strings.HasPrefix(request, "GET ") {
			t.Fatalf("readiness wrote state: %s", request)
		}
	}
}
