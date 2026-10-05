// Package status exposes unauthenticated process health probes.
package status

import (
	"context"
	"encoding/json/v2"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
)

// probe is the JSON body of every health probe. Version names the running
// build, so the status page can record a marker whenever it changes.
type probe struct {
	Status  string `json:"status"`
	Version string `json:"version"`
	Backend string `json:"backend"`
}

type Options struct {
	Backend string
	Check   func(context.Context) error
}

// Mount registers the liveness and readiness probes used by the platform.
func Mount(mux *http.ServeMux, deps core.Deps, options ...Options) {
	opts := Options{Backend: "memstore"}
	if len(options) > 0 {
		opts = options[0]
	}
	ok := probeBody("ok", opts.Backend)
	unavailable := probeBody("unavailable", opts.Backend)
	live := func(w http.ResponseWriter, _ *http.Request) {
		writeStatus(w, http.StatusOK, ok)
	}
	ready := func(w http.ResponseWriter, r *http.Request) {
		if deps.Store == nil {
			writeStatus(w, http.StatusServiceUnavailable, unavailable)
			return
		}
		if opts.Check != nil {
			ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
			defer cancel()
			if err := opts.Check(ctx); err != nil {
				writeStatus(w, http.StatusServiceUnavailable, unavailable)
				return
			}
		}
		writeStatus(w, http.StatusOK, ok)
	}
	mux.HandleFunc("GET /health/live", live)
	mux.HandleFunc("GET /api/v1/status/live", live)
	mux.HandleFunc("GET /health/ready", ready)
	mux.HandleFunc("GET /api/v1/status/ready", ready)
}

// probeBody prebuilds a probe response once; the build version never changes
// while the process runs.
func probeBody(status, backend string) []byte {
	body, err := json.Marshal(probe{Status: status, Version: buildinfo.Version(), Backend: backend})
	if err != nil {
		panic("status: encode probe: " + err.Error())
	}
	return append(body, '\n')
}

func writeStatus(w http.ResponseWriter, status int, body []byte) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_, _ = w.Write(body)
}
