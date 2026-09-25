package status

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestLivenessRoutesDoNotDependOnReadiness(t *testing.T) {
	mux := http.NewServeMux()
	Mount(mux, core.Deps{})

	for _, path := range []string{"/health/live", "/api/v1/status/live"} {
		t.Run(path, func(t *testing.T) {
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
			assertStatus(t, response, http.StatusOK, "ok")
		})
	}
}

func TestReadinessRequiresTheInMemoryStore(t *testing.T) {
	for _, test := range []struct {
		name string
		deps core.Deps
		want int
		body string
	}{
		{name: "missing store", want: http.StatusServiceUnavailable, body: "unavailable"},
		{name: "demo store", deps: core.Deps{Store: core.NewStore()}, want: http.StatusOK, body: "ok"},
	} {
		t.Run(test.name, func(t *testing.T) {
			mux := http.NewServeMux()
			Mount(mux, test.deps)
			for _, path := range []string{"/health/ready", "/api/v1/status/ready"} {
				response := httptest.NewRecorder()
				mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
				assertStatus(t, response, test.want, test.body)
			}
		})
	}
}

func assertStatus(t *testing.T, response *httptest.ResponseRecorder, wantCode int, wantBody string) {
	t.Helper()
	if response.Code != wantCode {
		t.Fatalf("status = %d, want %d", response.Code, wantCode)
	}
	if got := response.Header().Get("Content-Type"); got != "application/json; charset=utf-8" {
		t.Errorf("content type = %q", got)
	}
	var body struct {
		Status string `json:"status"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
		t.Fatalf("decode response: %v", err)
	}
	if body.Status != wantBody {
		t.Errorf("body status = %q, want %q", body.Status, wantBody)
	}
}
