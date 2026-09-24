package engine

import (
	"errors"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/ui"
	sdk "terrascale.tech/sdk"
)

func TestEngineRendersScopedHealthAndRegionCatalogIndependently(t *testing.T) {
	provider := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/v1/health":
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"region":"GRU","healthy":false,"version":"v-test"}`))
		case "/api/v1/regions":
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":true}],"hosts":{"GRU":"gru.example.test"}}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer provider.Close()

	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: "engine-user", Name: "Engine user", Email: "engine@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		t.Fatal(err)
	}
	csrf := engineTestCSRF{}
	renderer, err := render.New(layouts, store, csrf)
	if err != nil {
		t.Fatal(err)
	}
	factory := engineTestSDK{baseURL: provider.URL, httpClient: provider.Client()}
	deps := core.Deps{Store: store, Sessions: engineTestSessions{account.Principal()}, CSRF: csrf, Render: renderer, SDK: factory}
	mux := http.NewServeMux()
	Mount(mux, deps)

	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/engine?page=1", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("engine GET returned %d: %s", response.Code, response.Body.String())
	}
	for _, expected := range []string{"Member health", "Unhealthy", "v-test", "Region catalog", "GRU", "Sao Paulo", "1 regions · 1 hosts"} {
		if !strings.Contains(response.Body.String(), expected) {
			t.Errorf("engine page missing %q", expected)
		}
	}
	if strings.Contains(response.Body.String(), "Metric surface") || strings.Contains(response.Body.String(), "/internal/metrics") {
		t.Fatal("engine page exposed a metrics surface outside the ts-go contract")
	}
}

type engineTestSDK struct {
	baseURL    string
	httpClient *http.Client
}

func (f engineTestSDK) ForScope(tenantID, projectID string) (*sdk.Client, error) {
	return sdk.NewClient(sdk.Config{
		APIBaseURL: f.baseURL, AccessToken: "test-token", TenantID: tenantID,
		ProjectID: projectID, HTTPClient: f.httpClient,
	})
}

type engineTestSessions struct{ principal core.Principal }

func (s engineTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return errors.New("unused")
}
func (s engineTestSessions) Principal(*http.Request) (core.Principal, bool) { return s.principal, true }
func (s engineTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (engineTestSessions) End(http.ResponseWriter, *http.Request) {}
func (engineTestSessions) RevokeUser(string, string)              {}

type engineTestCSRF struct{}

func (engineTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (engineTestCSRF) Verify(*http.Request) error                      { return nil }
func (engineTestCSRF) Protect(next http.Handler) http.Handler          { return next }
