package main

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
)

func scrape(shape, body, seals, mirrored, cpu float64) string {
	return fmt.Sprintf(`terrabase_storage_reads_total{path="shape"} %v
terrabase_storage_reads_total{path="shape_body"} %v
terrabase_shape_seals_total %v
terrabase_shape_mirror_lag_seconds_count %v
terrabase_shape_requests_waiting 3
dotnet_process_cpu_time_seconds_total{cpu_mode="user"} %v
dotnet_process_cpu_time_seconds_total{cpu_mode="system"} %v
dotnet_process_memory_working_set_bytes 104857600
`, shape, body, seals, mirrored, cpu, cpu)
}

// A node's step is its counter deltas; reads above one per sealed object per location (node plus mirrored) break the
// bound, and a counter that fell fails the step.
func TestNodeDeltaAndBound(t *testing.T) {
	t.Parallel()
	nd, err := nodeDelta("gru", scrape(10, 5, 2, 0, 1), scrape(10, 5, 2, 0, 1), scrape(10, 5, 2, 0, 4), scrape(14, 7, 6, 0, 5))
	if err != nil || nd.Reads["shape"] != 4 || nd.Reads["shape_body"] != 2 || nd.Sealed != 4 || nd.Parked != 3 || nd.CPUPct != 20 || nd.RSSMB != 100 {
		t.Fatalf("delta %+v, err %v", nd, err)
	}
	if over := overBound([]nodeStep{nd}); len(over) != 1 || !strings.Contains(over[0], "1.50 per object") {
		t.Fatalf("over %v", over)
	}
	nd.Mirrored = 2
	if over := overBound([]nodeStep{nd}); len(over) != 0 {
		t.Fatalf("mirrored objects are a second location: %v", over)
	}
	if _, err := nodeDelta("gru", scrape(10, 5, 2, 0, 1), "x", "x", scrape(9, 5, 2, 0, 1)); err == nil {
		t.Fatal("a fallen counter is accepted")
	}
}

// Shards merge into one row per step: counts add, missed is admitted readers times writes less deliveries, and a step
// over the bound fails the run with its reason.
func TestMergeReportSumsShardsAndFailsOverBound(t *testing.T) {
	t.Parallel()
	hist := func(v int64) *hdrhistogram.Snapshot {
		h := hdrhistogram.New(1, 120_000_000, 3)
		_ = h.RecordValue(v)
		return h.Export()
	}
	within := nodeStep{Node: "gru", Reads: map[string]int64{"shape": 1, "shape_body": 1}, Sealed: 2}
	shards := [][]shardStep{
		{{N: 3, Admitted: 2, Delivered: 19, Writes: 10, Hist: hist(1000), Codes: map[int]int64{200: 3, 304: 1}, Nodes: []nodeStep{within}}},
		{{N: 3, Admitted: 1, Delivered: 10, Hist: hist(3000), Codes: map[int]int64{200: 4}}},
	}
	out := filepath.Join(t.TempDir(), "r")
	if err := mergeReport(out, shards); err != nil {
		t.Fatalf("within bound: %v", err)
	}
	shards[0][0].Nodes[0].Reads["shape"] = 9
	err := mergeReport(out, shards)
	if err == nil || !strings.Contains(err.Error(), "N=3 gru: 10 shape storage reads") {
		t.Fatalf("over bound: %v", err)
	}
	rows := v3Table([]v3Row{{N: 3, Missed: 1, Nodes: []nodeStep{within}}})
	if !strings.Contains(rows, "| 3 | 0 | 0 | 0 | 0 | 1 |") || !strings.Contains(rows, "within bound") {
		t.Fatalf("table %s", rows)
	}
}

// After admission, a live-load reader answered 409 must_refetch bootstraps again at offset=-1 instead of failing.
func TestFollowShapeRefetchesAfterMustRefetch(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	var bootstraps atomic.Int64
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch q := r.URL.Query(); {
		case q.Get("offset") == "-1":
			if bootstraps.Add(1) == 2 {
				cancel()
			}
			w.Header().Set("TB-Shape-Handle", testHandle)
			w.Header().Set("Location", "/v1/projects/p/databases/d/collections/c/shape?handle="+testHandle+"&offset=snap.5.0")
			w.WriteHeader(http.StatusTemporaryRedirect)
		case q.Get("offset") == "snap.5.0":
			fmt.Fprint(w, shapeReply("5", false, ""))
		default:
			w.WriteHeader(http.StatusConflict)
			fmt.Fprint(w, `{"error":{"code":"must_refetch","message":"below the floor","handle":"`+testHandle+`"}}`)
		}
	}))
	defer srv.Close()
	b := testBench(srv.URL)
	b.admitBy = time.Now()
	m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3)}
	b.followShape(ctx, m, make(chan string, 1))
	if bootstraps.Load() < 2 || m.failures.Load() != 0 || m.codes[409].Load() == 0 {
		t.Fatalf("bootstraps %d, failures %d (%v), 409s %d", bootstraps.Load(), m.failures.Load(), m.firstErr(), m.codes[409].Load())
	}
}

// Before admitBy, a live-load reader repeats a shape_busy refusal instead of giving up, and counts each repeat.
func TestFollowShapeRepeatsRefusalUntilAdmitBy(t *testing.T) {
	t.Parallel()
	var calls atomic.Int64
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusTooManyRequests)
		fmt.Fprint(w, `{"error":{"code":"shape_busy","message":"busy"}}`)
	}))
	defer srv.Close()
	b := testBench(srv.URL)
	b.admitBy = time.Now().Add(500 * time.Millisecond)
	m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3)}
	outcomes := make(chan string, 1)
	b.followShape(t.Context(), m, outcomes)
	if got := <-outcomes; got != "rejected_shape_busy" || m.refused.Load() < 2 || m.codes[429].Load() != calls.Load() {
		t.Fatalf("outcome %q, refused %d, 429s %d of %d", got, m.refused.Load(), m.codes[429].Load(), calls.Load())
	}
}
