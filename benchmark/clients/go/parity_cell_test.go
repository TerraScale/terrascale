package main

import (
	"context"
	"fmt"
	"net/http"
	"slices"
	"sync/atomic"
	"testing"
	"time"
)

var parityCellTestPlan = parityPlan{Warmup: 100 * time.Millisecond, Measure: 300 * time.Millisecond, Drain: 10 * time.Second}

func TestParityCellAcceptsExactReadsOnEveryLane(t *testing.T) {
	t.Parallel()
	for _, lane := range []string{"raw-h1", "raw-h2", "sdk"} {
		for _, concurrency := range []int{1, 16} {
			t.Run(fmt.Sprintf("%s-c%d", lane, concurrency), func(t *testing.T) {
				t.Parallel()
				target := parityTestTarget(lane)
				f := newParityFixture(t, func(_ int64, w http.ResponseWriter, r *http.Request) { serveDocument(target, "gru", w, r) })
				plan := parityCellTestPlan
				plan.Concurrency = concurrency
				row, err := parityCell(context.Background(), parityTestToken("a.b.c"), f.transport(), target, plan)
				if err != nil || len(row.Rejected) != 0 || row.Transport.Attempts != row.Starts || row.Measured == 0 {
					t.Fatalf("row %+v, %v", row, err)
				}
				if lane == "raw-h1" && row.Transport.H1 != row.Starts || lane != "raw-h1" && row.Transport.H2 != row.Starts {
					t.Fatalf("protocol counts %+v", row.Transport)
				}
			})
		}
	}
}

// Each fault is hidden from the operation's result by the SDK or survives a
// 200 header; the cell must still be rejected for the named reason.
func TestParityCellRejectsHiddenRetriesAndMismatches(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name, lane, reason string
		serve              func(target parityTarget, n int64, w http.ResponseWriter, r *http.Request)
	}{
		{"sdk-body-read-retry-after-200", "sdk", "attempts_differ_from_starts", func(target parityTarget, n int64, w http.ResponseWriter, r *http.Request) {
			if n%7 != 0 {
				serveDocument(target, "gru", w, r)
				return
			}
			w.Header().Set("X-TerraBase-Region", "gru")
			w.Header().Set("Content-Length", "4096")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte(`{"id":`))
			w.(http.Flusher).Flush()
			panic(http.ErrAbortHandler)
		}},
		{"sdk-503-retry", "sdk", "bad_attempts", func(target parityTarget, n int64, w http.ResponseWriter, r *http.Request) {
			if n%7 != 0 {
				serveDocument(target, "gru", w, r)
				return
			}
			w.Header().Set("X-TerraBase-Region", "gru")
			http.Error(w, `{"error":{"code":"capacity"}}`, http.StatusServiceUnavailable)
		}},
		{"wrong-region", "raw-h2", "bad_attempts", func(target parityTarget, n int64, w http.ResponseWriter, r *http.Request) {
			serveDocument(target, map[bool]string{true: "fra", false: "gru"}[n > 1], w, r)
		}},
		{"wrong-version", "raw-h1", "errors", func(target parityTarget, n int64, w http.ResponseWriter, r *http.Request) {
			if n > 1 {
				target.Document.version = "tbv2-other"
			}
			serveDocument(target, "gru", w, r)
		}},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			target := parityTestTarget(test.lane)
			f := newParityFixture(t, func(n int64, w http.ResponseWriter, r *http.Request) { test.serve(target, n, w, r) })
			plan := parityCellTestPlan
			plan.Concurrency = 4
			row, err := parityCell(context.Background(), parityTestToken("a.b.c"), f.transport(), target, plan)
			if err != nil || !slices.Contains(row.Rejected, test.reason) {
				t.Fatalf("rejected %v, %v; want %s in row %+v", row.Rejected, err, test.reason, row)
			}
			if test.name == "sdk-body-read-retry-after-200" && (row.OK != row.Measured || row.Transport.Bad != 0 || row.Transport.BodyErrors == 0) {
				t.Fatalf("control did not hide the retry behind ok results: %+v", row)
			}
		})
	}
}

// refreshingToken models a tsauth source: a 401 invalidates the first token once.
type refreshingToken struct{ invalidated atomic.Bool }

func (s *refreshingToken) Token(ctx context.Context) (string, error) {
	if s.invalidated.Load() {
		return "a.b.fresh", ctx.Err()
	}
	return "a.b.stale", ctx.Err()
}

func (s *refreshingToken) Invalidate(string) { s.invalidated.Store(true) }

// The shipped SDK refresh still runs through the parity transport.
func TestParityCellKeepsSDKTokenRefresh(t *testing.T) {
	t.Parallel()
	target := parityTestTarget("sdk")
	f := newParityFixture(t, func(_ int64, w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer a.b.fresh" {
			http.Error(w, `{"error":{"code":"unauthorized"}}`, http.StatusUnauthorized)
			return
		}
		serveDocument(target, "gru", w, r)
	})
	source := &refreshingToken{}
	plan := parityCellTestPlan
	plan.Concurrency = 1
	row, err := parityCell(context.Background(), source, f.transport(), target, plan)
	if err != nil || !source.invalidated.Load() || len(row.Rejected) != 0 {
		t.Fatalf("refresh %v, row %+v, %v", source.invalidated.Load(), row, err)
	}
}
