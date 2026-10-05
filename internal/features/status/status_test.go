package status

import (
	"encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
)

func TestProbesReportStatusAndBuildVersion(t *testing.T) {
	ready := http.NewServeMux()
	Mount(ready, core.Deps{Store: memstore.New()})
	unready := http.NewServeMux()
	Mount(unready, core.Deps{})
	for _, tc := range []struct {
		name   string
		mux    *http.ServeMux
		path   string
		code   int
		status string
	}{
		{"live", ready, "/health/live", http.StatusOK, "ok"},
		{"live api alias", ready, "/api/v1/status/live", http.StatusOK, "ok"},
		{"ready", ready, "/health/ready", http.StatusOK, "ok"},
		{"ready api alias", ready, "/api/v1/status/ready", http.StatusOK, "ok"},
		{"live without store", unready, "/health/live", http.StatusOK, "ok"},
		{"ready without store", unready, "/health/ready", http.StatusServiceUnavailable, "unavailable"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			recorder := httptest.NewRecorder()
			tc.mux.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, tc.path, nil))
			if recorder.Code != tc.code {
				t.Fatalf("status code = %d, want %d", recorder.Code, tc.code)
			}
			if got := recorder.Header().Get("Content-Type"); got != "application/json; charset=utf-8" {
				t.Fatalf("Content-Type = %q", got)
			}
			var body probe
			if err := json.Unmarshal(recorder.Body.Bytes(), &body); err != nil {
				t.Fatalf("decode %q: %v", recorder.Body.String(), err)
			}
			if body.Status != tc.status {
				t.Fatalf("status = %q, want %q", body.Status, tc.status)
			}
			if body.Backend != "memstore" {
				t.Fatalf("backend = %q, want memstore", body.Backend)
			}
			if body.Version == "" || body.Version != buildinfo.Version() {
				t.Fatalf("version = %q, want %q", body.Version, buildinfo.Version())
			}
		})
	}
}
