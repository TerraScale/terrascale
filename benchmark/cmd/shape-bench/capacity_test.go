package main

import (
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"terrascale.tech/ts-benchmark/internal/apibench"
)

// A readiness timeout after admission is a capacity limit; the same timeout before a step is an invalid measurement.
func TestAwaitParkedTimeoutAfterAdmissionIsACapacityLimit(t *testing.T) {
	t.Parallel()
	m := &measure{}
	m.atHead.Store(1)
	_, err := awaitParked(t.Context(), gaugeServer(t, func(int64) int64 { return 0 }), m, 2, 300*time.Millisecond)
	var limit capacityLimit
	if !errors.As(err, &limit) {
		t.Fatalf("after admission: err %v, want a capacity limit", err)
	}
	_, err = awaitParked(t.Context(), gaugeServer(t, func(int64) int64 { return 1 }), &measure{}, 0, 300*time.Millisecond)
	if err == nil || errors.As(err, &limit) {
		t.Fatalf("before the step: err %v, want an invalid measurement", err)
	}
}

// No follower admitted is a valid row: the rejections and the reason are kept, and nothing the window never measured is
// reported.
func TestStepRecordsNoAdmissionAsARow(t *testing.T) {
	t.Parallel()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, "/metrics") {
			fmt.Fprint(w, "terrabase_stats_operations_total{kind=\"read\"} 0\nterrabase_shape_requests_waiting 0\n")
			return
		}
		w.WriteHeader(http.StatusTooManyRequests)
		fmt.Fprint(w, `{"error":{"code":"shape_busy"}}`)
	}))
	t.Cleanup(srv.Close)
	b := &bench{
		collection: "c",
		database:   srv.URL + "/v1/projects/p/databases/d",
		token:      "t",
		metrics:    srv.URL + "/metrics",
		shape:      srv.Client(),
	}
	res, p, err := b.step(t.Context(), apibench.Step{Scenario: "shape.fanout", Transport: "http1-long-poll", PayloadBytes: 256, Concurrency: 1, WarmupS: 5, StepS: 30})
	if err != nil {
		t.Fatalf("step err %v, want a row", err)
	}
	if res.Stopped != "no follower of 1 admitted" || res.Status["rejected_shape_busy"] != 1 || res.Status["admitted"] != 0 || res.Ops != 0 {
		t.Fatalf("row %+v, want the rejection and the reason", res)
	}
	if _, ok := res.Status["writes"]; ok || p.after != "" || p.before == "" {
		t.Fatalf("window figures present on a row with no window: status %v, probe after %q", res.Status, p.after)
	}
}

func TestNotesMarksUnmeasuredRowsAsNA(t *testing.T) {
	t.Parallel()
	res := []apibench.Result{{Scenario: "shape.fanout", Concurrency: 3, Stopped: "no follower of 3 admitted",
		Status: map[string]int64{"rejected_shape_busy": 3, "qos_busy_followers": 2}}}
	text := notes(res, []probe{{n: 3, target: "shape.fanout", before: "b\n"}}, 1, "http://127.0.0.1:1/metrics", "Commit: c")
	if want := "| shape.fanout | 3 | 0 | 3 shape_busy | n/a | n/a | n/a | n/a | n/a | n/a | n/a | no follower of 3 admitted |"; !strings.Contains(text, want) {
		t.Fatal(text)
	}
	if !strings.Contains(text, "shape.fanout N=3: no window") {
		t.Fatal(text)
	}
}
