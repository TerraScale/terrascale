// Package status exposes unauthenticated process health probes.
package status

import (
	"encoding/json"
	"net/http"

	"terrascale.tech/dashboard/internal/core"
)

type response struct {
	Status string `json:"status"`
}

// Mount registers the liveness and readiness probes used by the platform.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /health/live", live)
	mux.HandleFunc("GET /api/v1/status/live", live)
	mux.HandleFunc("GET /health/ready", ready(deps))
	mux.HandleFunc("GET /api/v1/status/ready", ready(deps))
}

func live(w http.ResponseWriter, _ *http.Request) {
	writeStatus(w, http.StatusOK, "ok")
}

func ready(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		if deps.Store == nil {
			writeStatus(w, http.StatusServiceUnavailable, "unavailable")
			return
		}
		writeStatus(w, http.StatusOK, "ok")
	}
}

func writeStatus(w http.ResponseWriter, status int, value string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(response{Status: value})
}
