// Package status exposes unauthenticated process health probes.
package status

import (
	"context"
	"encoding/json/v2"
	"log/slog"
	"net/http"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
)

const (
	checkEvery    = 5 * time.Second
	checkTimeout  = 3 * time.Second
	checkFailures = 3
	// checkStale marks readiness unavailable when the watch loop itself stops.
	checkStale = 20 * time.Second
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

// Mount registers the liveness and readiness probes used by the platform. The
// Check runs in a background loop that stops when ctx ends; probes never call it.
func Mount(ctx context.Context, mux *http.ServeMux, deps core.Deps, options ...Options) {
	if watched := mount(mux, deps, options...); watched != nil && deps.Store != nil {
		go watched.run(ctx, checkEvery)
	}
}

// mount registers the probes and returns the watch that answers readiness,
// or nil when there is no Check to watch.
func mount(mux *http.ServeMux, deps core.Deps, options ...Options) *watch {
	opts := Options{Backend: "memstore"}
	if len(options) > 0 {
		opts = options[0]
	}
	var watched *watch
	if opts.Check != nil {
		watched = &watch{check: opts.Check}
	}
	ok := probeBody("ok", opts.Backend)
	unavailable := probeBody("unavailable", opts.Backend)
	live := func(w http.ResponseWriter, _ *http.Request) {
		writeStatus(w, http.StatusOK, ok)
	}
	ready := func(w http.ResponseWriter, _ *http.Request) {
		if deps.Store == nil || (watched != nil && !watched.available(time.Now())) {
			writeStatus(w, http.StatusServiceUnavailable, unavailable)
			return
		}
		writeStatus(w, http.StatusOK, ok)
	}
	mux.HandleFunc("GET /health/live", live)
	mux.HandleFunc("GET /api/v1/status/live", live)
	mux.HandleFunc("GET /health/ready", ready)
	mux.HandleFunc("GET /api/v1/status/ready", ready)
	return watched
}

// watch keeps the latest Check result so readiness answers without a round trip.
type watch struct {
	check func(context.Context) error

	mu       sync.Mutex
	checked  time.Time
	failures int
}

// run checks once right away, then once per every, until ctx ends.
func (w *watch) run(ctx context.Context, every time.Duration) {
	ticker := time.NewTicker(every)
	defer ticker.Stop()
	for {
		checkCtx, cancel := context.WithTimeout(ctx, checkTimeout)
		err := w.check(checkCtx)
		cancel()
		if ctx.Err() == nil {
			w.record(time.Now(), err)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

// record stores one check result and logs only the transitions an operator needs.
func (w *watch) record(now time.Time, err error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.checked = now
	if err == nil {
		if w.failures >= checkFailures {
			slog.Info("TerraBase readiness recovered", "component", "status", "failures", w.failures)
		}
		w.failures = 0
		return
	}
	w.failures++
	if w.failures == checkFailures {
		slog.Warn("TerraBase readiness unavailable after repeated failures", "component", "status", "failures", w.failures, "error", err)
		return
	}
	slog.Debug("TerraBase readiness check failed", "component", "status", "failures", w.failures, "error", err)
}

// available reports whether the latest check passed, is recent, and no
// failure streak has reached checkFailures.
func (w *watch) available(now time.Time) bool {
	w.mu.Lock()
	defer w.mu.Unlock()
	return !w.checked.IsZero() && w.failures < checkFailures && now.Sub(w.checked) <= checkStale
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
