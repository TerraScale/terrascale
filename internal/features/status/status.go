// Package status exposes unauthenticated process health probes.
package status

import (
	"net/http"

	"terrascale.tech/dashboard/internal/core"
)

// Probe bodies never change, so they are written as prebuilt JSON.
var (
	okBody          = []byte(`{"status":"ok"}` + "\n")
	unavailableBody = []byte(`{"status":"unavailable"}` + "\n")
)

// Mount registers the liveness and readiness probes used by the platform.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /health/live", live)
	mux.HandleFunc("GET /api/v1/status/live", live)
	mux.HandleFunc("GET /health/ready", ready(deps))
	mux.HandleFunc("GET /api/v1/status/ready", ready(deps))
}

func live(w http.ResponseWriter, _ *http.Request) {
	writeStatus(w, http.StatusOK, okBody)
}

func ready(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		if deps.Store == nil {
			writeStatus(w, http.StatusServiceUnavailable, unavailableBody)
			return
		}
		writeStatus(w, http.StatusOK, okBody)
	}
}

func writeStatus(w http.ResponseWriter, status int, body []byte) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_, _ = w.Write(body)
}
