package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

// Startup arrives before ready; every N runs even when an earlier one breaches the error rate; an invalid measurement
// stops the run and keeps the completed results.
func TestSweepRunsEveryFollowerCountUntilAnInvalidMeasurement(t *testing.T) {
	t.Parallel()
	var ran []int
	var want []int
	for range targets {
		want = append(want, followers...)
	}
	startup, results, err := sweep(apibench.Startup{Type: "startup", InitMS: 12}, apibench.Ready{Client: "test"}, func(st apibench.Step) (apibench.Result, error) {
		ran = append(ran, st.Concurrency)
		return apibench.Result{Concurrency: st.Concurrency, ErrorRate: 0.5}, nil
	})
	if err != nil || startup.InitMS != 12 || !slices.Equal(ran, want) || len(results) != len(targets)*len(followers) || results[0].Stopped != "error rate" {
		t.Fatalf("ran %v, %d results, err %v", ran, len(results), err)
	}
	ran = nil
	_, results, err = sweep(apibench.Startup{Type: "startup"}, apibench.Ready{Client: "test"}, func(st apibench.Step) (apibench.Result, error) {
		ran = append(ran, st.Concurrency)
		if st.Concurrency == 10 {
			return apibench.Result{}, errors.New("invalid")
		}
		return apibench.Result{}, nil
	})
	if err == nil || !strings.Contains(err.Error(), "N=10: invalid") || !slices.Equal(ran, []int{1, 10}) || len(results) != 1 {
		t.Fatalf("ran %v, err %v", ran, err)
	}
}

func TestReadSSEJoinsDataAndDefaultsTheEventType(t *testing.T) {
	t.Parallel()
	stream := "event: cursor\nid: c1\ndata: {\"cursor\":\"c1\"}\n\nevent: change\ndata: a\ndata: b\n\ndata:plain\n\n: comment\nevent:empty\ndata\n\n"
	var got []string
	if err := readSSE(strings.NewReader(stream), func(event string, data []byte) error {
		got = append(got, event+"="+string(data))
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if want := []string{`cursor={"cursor":"c1"}`, "change=a\nb", "message=plain", "empty="}; !slices.Equal(got, want) {
		t.Fatalf("got %q, want %q", got, want)
	}
}

// Only in-window writes count, a redelivery is counted as a document read but not as a second delivery, and an event
// that is not a writer upsert is an invalid measurement.
func TestDeliverCountsFirstInWindowDeliveries(t *testing.T) {
	t.Parallel()
	start := time.Unix(100, 0)
	m := &measure{start: start, end: start.Add(10 * time.Second), deadline: start.Add(15 * time.Second), hist: hdrhistogram.New(1, 60_000_000, 3)}
	event := func(id string, sent time.Time) []byte {
		b, _ := json.Marshal(map[string]any{"id": id, "op": "upsert", "document": map[string]any{"t": sent.UnixNano()}})
		return b
	}
	seen := map[string]bool{}
	for _, e := range [][]byte{event("a", start.Add(-time.Second)), event("b", start.Add(time.Second)), event("b", start.Add(time.Second)), event("c", start.Add(10*time.Second))} {
		if err := m.deliver(seen, e, start.Add(3*time.Second)); err != nil {
			t.Fatal(err)
		}
	}
	if m.ok != 1 || m.events != 2 || m.hist.ValueAtQuantile(50) < 1_999_000 || m.hist.ValueAtQuantile(50) > 2_001_000 {
		t.Fatalf("ok %d, events %d, p50 %d µs", m.ok, m.events, m.hist.ValueAtQuantile(50))
	}
	if err := m.deliver(seen, event("e", start.Add(2*time.Second)), start.Add(16*time.Second)); err != nil || m.ok != 1 {
		t.Fatalf("a delivery after the deadline: ok %d, %v", m.ok, err)
	}
	if err := m.deliver(seen, []byte(`{"id":"d","op":"delete","document":null}`), start); err == nil {
		t.Fatal("a delete counted as a delivery")
	}
}

func TestPromValueReadsCounterAndGaugeSamples(t *testing.T) {
	t.Parallel()
	body := `# TYPE terrabase_stats_operations_total counter
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="read"} 42 1700000000000
terrabase_stats_operations_total{otel_scope_name="TerraScale.TerraBase",kind="write"} 7 1700000000000
terrabase_changes_streams{otel_scope_name="TerraScale.TerraBase"} 32 1700000000000
terrabase_changes_streams_other 9
`
	if v, ok := promValue(body, "terrabase_stats_operations", `kind="read"`); !ok || v != 42 {
		t.Fatalf("reads %v %v", v, ok)
	}
	if v, ok := promValue(body, "terrabase_changes_streams", ""); !ok || v != 32 {
		t.Fatalf("streams %v %v", v, ok)
	}
	if _, ok := promValue(body, "terrabase_missing", ""); ok {
		t.Fatal("missing metric found")
	}
}

// The feed's count fails closed: an absent feed series, a path outside the bounded set, a path without a label and a
// repeated path are errors; a counter that fell is an error too.
func TestFeedReadsFailsClosed(t *testing.T) {
	t.Parallel()
	const point = `terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 4 1700000000000` + "\n"
	const feed = `terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 17 1700000000000` + "\n"
	if v, err := feedReads(point + feed); err != nil || v != 17 {
		t.Fatalf("feed %v, %v", v, err)
	}
	if _, err := feedReads(point); err == nil {
		t.Fatal("an absent feed series read as a count")
	}
	if _, err := feedReads(point + feed + `terrabase_storage_reads_total{otel_scope_name="x",path="tenant_a"} 1` + "\n"); err == nil {
		t.Fatal("an unbounded path label accepted")
	}
	if _, err := feedReads(feed + feed); err == nil {
		t.Fatal("two feed series accepted")
	}
	if _, err := feedReads(`terrabase_storage_reads_total{otel_scope_name="x"} 1` + "\n" + feed); err == nil {
		t.Fatal("a sample without a path label accepted")
	}
	if d, err := feedDelta(17, 40); err != nil || d != 23 {
		t.Fatalf("delta %d, %v", d, err)
	}
	if _, err := feedDelta(40, 17); err == nil {
		t.Fatal("a fallen counter accepted")
	}
}

// The storage delta is the table's first measured read column, ahead of the accounting delta it no longer replaces.
func TestNotesTableOrdersStorageReadsBeforeTheSecondaryColumns(t *testing.T) {
	t.Parallel()
	res := []apibench.Result{{Scenario: "changes.fanout", Concurrency: 10, Status: map[string]int64{"admitted": 10,
		"storage_reads_delta": 1234, "accounting_reads_delta": 5, "inferred_min_doc_selects": 6}}}
	text := notes(res, []probe{{n: 10, target: "changes.fanout", before: "b\n", after: "a\n"}}, 1, "http://127.0.0.1:1/metrics", "Commit: c")
	if !strings.Contains(text, "| changes.fanout | 10 | 10 | 0 | 0 | 0 | 0 | 0.00 | 1234 | 5 | 6 |") {
		t.Fatal(text)
	}
}

func TestListenPortsKeepsTheProcessListeners(t *testing.T) {
	t.Parallel()
	table := `  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 111 1 0000000000000000 100 0 0 10 0
   1: 00000000:1F91 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 222 1 0000000000000000 100 0 0 10 0
   2: 0100007F:1F92 0100007F:9C40 01 00000000:00000000 00:00000000 00000000  1000        0 111 1 0000000000000000 100 0 0 10 0
`
	if got := listenPorts(table, map[string]bool{"111": true}); !slices.Equal(got, []int{8080}) {
		t.Fatalf("ports %v", got)
	}
}

func TestStreamCountRejectsAmbiguousSeries(t *testing.T) {
	t.Parallel()
	const line = "terrabase_changes_streams{otel_scope_name=\"TerraScale.TerraBase\"} 1\n"
	if v, ok := streamCount(line); !ok || v != 1 {
		t.Fatalf("one stream: %v %v", v, ok)
	}
	if _, ok := streamCount(line + line); ok {
		t.Fatal("ambiguous stream series accepted")
	}
}

func TestDocumentIsPayloadSizedJSONWithItsSendTime(t *testing.T) {
	t.Parallel()
	sent := time.Unix(1_800_000_000, 123)
	b := document(sent)
	var d struct {
		T int64 `json:"t"`
	}
	if len(b) != payloadBytes || json.Unmarshal(b, &d) != nil || d.T != sent.UnixNano() {
		t.Fatalf("document %d bytes %s", len(b), b)
	}
}

func TestErrorCodeReadsTheBodyCode(t *testing.T) {
	t.Parallel()
	if c := errorCode(strings.NewReader(`{"error":{"code":"changes_busy","message":"m"}}`)); c != "changes_busy" {
		t.Fatal(c)
	}
	if c := errorCode(strings.NewReader("not json")); c != "unknown" {
		t.Fatal(c)
	}
}

// Only the feed's own cap counts as a rejection; an admitted follower reports once its first cursor arrives.
func TestFollowClassifiesAdmission(t *testing.T) {
	t.Parallel()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Query().Get("collections") {
		case "open":
			w.Header().Set("Content-Type", "text/event-stream")
			fmt.Fprint(w, "event: cursor\nid: c\ndata: {}\n\n")
			w.(http.Flusher).Flush()
			<-r.Context().Done()
		case "busy":
			w.WriteHeader(http.StatusTooManyRequests)
			fmt.Fprint(w, `{"error":{"code":"changes_busy"}}`)
		default:
			w.WriteHeader(http.StatusTooManyRequests)
			fmt.Fprint(w, `{"error":{"code":"tenant_memory"}}`)
		}
	}))
	defer srv.Close()
	for _, c := range []struct{ collection, want string }{{"open", "admitted"}, {"busy", "rejected_changes_busy"}, {"memory", ""}} {
		b := &bench{database: srv.URL, target: targets[0], collection: c.collection, sse: srv.Client()}
		m := &measure{}
		ctx, cancel := context.WithCancel(t.Context())
		outcomes := make(chan string, 1)
		done := make(chan struct{})
		go func() {
			defer close(done)
			b.target.follow(b, ctx, m, outcomes)
		}()
		got := <-outcomes
		cancel()
		<-done
		if got != c.want || (m.err != nil) != (c.want == "") {
			t.Fatalf("%s: outcome %q, err %v", c.collection, got, m.err)
		}
	}
}
