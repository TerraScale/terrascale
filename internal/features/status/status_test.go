package status

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
)

var errDown = errors.New("terrabase down")

func TestProbesReportStatusAndBuildVersion(t *testing.T) {
	ready := http.NewServeMux()
	Mount(context.Background(), ready, core.Deps{Store: memstore.New()})
	unready := http.NewServeMux()
	Mount(context.Background(), unready, core.Deps{})
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

func readyCode(mux *http.ServeMux) int {
	recorder := httptest.NewRecorder()
	mux.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/health/ready", nil))
	return recorder.Code
}

func TestReadyNeverCallsCheckAndWaitsForFirstResult(t *testing.T) {
	var calls atomic.Int32
	check := func(context.Context) error { calls.Add(1); return nil }
	mux := http.NewServeMux()
	watched := mount(mux, core.Deps{Store: memstore.New()}, Options{Backend: "terrabase", Check: check})
	if got := readyCode(mux); got != http.StatusServiceUnavailable {
		t.Fatalf("ready before first check = %d, want 503", got)
	}
	watched.record(time.Now(), nil)
	for range 20 {
		if got := readyCode(mux); got != http.StatusOK {
			t.Fatalf("ready = %d, want 200", got)
		}
	}
	if got := calls.Load(); got != 0 {
		t.Fatalf("probes called Check %d times, want 0", got)
	}
}

func TestWatchRunsCheckInBackgroundUntilContextEnds(t *testing.T) {
	var calls atomic.Int32
	check := func(context.Context) error { calls.Add(1); return nil }
	mux := http.NewServeMux()
	watched := mount(mux, core.Deps{Store: memstore.New()}, Options{Backend: "terrabase", Check: check})
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		watched.run(ctx, time.Hour)
		close(done)
	}()
	deadline := time.Now().Add(5 * time.Second)
	for readyCode(mux) != http.StatusOK {
		if time.Now().After(deadline) {
			t.Fatal("ready never turned available after the first background check")
		}
		time.Sleep(time.Millisecond)
	}
	for range 20 {
		readyCode(mux)
	}
	if got := calls.Load(); got != 1 {
		t.Fatalf("Check ran %d times, want 1 (first check only, then the ticker)", got)
	}
	cancel()
	<-done
}

func TestReadyFollowsConsecutiveFailures(t *testing.T) {
	for _, tc := range []struct {
		name  string
		steps []error
		code  int
	}{
		{"one failure keeps ready", []error{errDown}, http.StatusOK},
		{"two failures keep ready", []error{errDown, errDown}, http.StatusOK},
		{"three failures are unready", []error{errDown, errDown, errDown}, http.StatusServiceUnavailable},
		{"recovery restores ready", []error{errDown, errDown, errDown, nil}, http.StatusOK},
		{"success resets the streak", []error{errDown, errDown, nil, errDown, errDown}, http.StatusOK},
	} {
		t.Run(tc.name, func(t *testing.T) {
			mux := http.NewServeMux()
			watched := mount(mux, core.Deps{Store: memstore.New()}, Options{Backend: "terrabase", Check: func(context.Context) error { return nil }})
			watched.record(time.Now(), nil)
			for _, err := range tc.steps {
				watched.record(time.Now(), err)
			}
			if got := readyCode(mux); got != tc.code {
				t.Fatalf("ready = %d, want %d", got, tc.code)
			}
		})
	}
}

func TestReadyTurnsUnavailableWhenResultIsStale(t *testing.T) {
	mux := http.NewServeMux()
	watched := mount(mux, core.Deps{Store: memstore.New()}, Options{Backend: "terrabase", Check: func(context.Context) error { return nil }})
	watched.record(time.Now().Add(-checkStale-time.Second), nil)
	if got := readyCode(mux); got != http.StatusServiceUnavailable {
		t.Fatalf("ready with stale result = %d, want 503", got)
	}
}

func TestWatchLogsOnlyReadinessTransitions(t *testing.T) {
	var logs bytes.Buffer
	previous := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, &slog.HandlerOptions{Level: slog.LevelDebug})))
	t.Cleanup(func() { slog.SetDefault(previous) })

	watched := &watch{check: func(context.Context) error { return nil }}
	for _, err := range []error{errDown, errDown, errDown, errDown, nil} {
		watched.record(time.Now(), err)
	}
	out := logs.String()
	for _, want := range []struct {
		line  string
		count int
	}{
		{`level=DEBUG msg="TerraBase readiness check failed" component=status`, 3},
		{`level=WARN msg="TerraBase readiness unavailable after repeated failures"`, 1},
		{`level=INFO msg="TerraBase readiness recovered"`, 1},
	} {
		if got := strings.Count(out, want.line); got != want.count {
			t.Fatalf("%d lines %q, want %d in:\n%s", got, want.line, want.count, out)
		}
	}
}
