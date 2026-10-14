package main

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
)

const testHandle = "h1-0123456789abcdef"

// shapeReply is a stub's response body in the shape of shape-reads.md section 6; ops is the comma-separated operations.
func shapeReply(next string, upToDate bool, ops string) string {
	return fmt.Sprintf(`{"shape_id":"a07ef7c2873b7b4c4fcbb88c9e682ca5","handle":%q,"offset":"0","next_offset":%q,"covered_offset":%q,"values_at":%q,"up_to_date":%t,"cursor_resume":"x","changes":[{"offset":%q,"ops":[%s]}]}`,
		testHandle, next, next, next, upToDate, next, ops)
}

// testBench is a follower's bench against one stub: the collection, token and both clients the follower uses.
func testBench(url string) *bench {
	return &bench{
		collection: "c",
		token:      "t",
		origin:     url,
		database:   url + "/v1/projects/p/databases/d",
		shape: &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}},
	}
}

// The follower bootstraps through the 307, pages its snapshot, catches up through a chunk, then long-polls. A busy live
// poll is repeated with its state kept, the next live poll carries the bucket the last answer named, and only the write
// sent inside the window is delivered: the snapshot's document was sent before it.
func TestFollowShapeBootstrapsPagesAndLivePolls(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	inWindow := time.Now()
	var busy atomic.Bool
	var cursor atomic.Value
	cursor.Store("")
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		q := r.URL.Query()
		if r.Header.Get("Authorization") != "Bearer t" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		switch {
		case q.Get("offset") == "-1" && q.Get("handle") == "":
			w.Header().Set("TB-Shape-Handle", testHandle)
			w.Header().Set("Location", "/v1/projects/p/databases/d/collections/c/shape?handle="+testHandle+"&offset=snap.5.0")
			w.WriteHeader(http.StatusTemporaryRedirect)
		case q.Get("offset") == "snap.5.0":
			fmt.Fprint(w, shapeReply("5", false, `{"op":"upsert","id":"01A","version":"v","document":{"t":1}}`))
		case q.Get("offset") == "5":
			fmt.Fprint(w, shapeReply("6", true, fmt.Sprintf(`{"op":"upsert","id":"01B","version":"v","document":{"t":%d}}`, inWindow.UnixNano())))
		case q.Get("live") == "true" && q.Get("cursor") == "":
			if busy.CompareAndSwap(false, true) {
				w.WriteHeader(http.StatusTooManyRequests)
				fmt.Fprint(w, `{"error":{"code":"shape_busy","message":"too many shape flights"}}`)
				return
			}
			w.Header().Set("TB-Shape-Cursor", "100")
			fmt.Fprint(w, shapeReply("6", true, ""))
		case q.Get("live") == "true" && q.Get("cursor") == "100":
			cursor.Store(q.Get("cursor"))
			cancel()
			w.Header().Set("TB-Shape-Cursor", "101")
			fmt.Fprint(w, shapeReply("6", true, ""))
		default:
			w.WriteHeader(http.StatusConflict)
			fmt.Fprint(w, `{"error":{"code":"must_refetch","message":"unexpected request","handle":"`+testHandle+`"}}`)
		}
	}))
	defer srv.Close()
	m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3), start: inWindow.Add(-time.Second), end: inWindow.Add(time.Hour)}
	m.deadline = m.end.Add(time.Hour)
	outcomes := make(chan string, 1)
	done := make(chan struct{})
	go func() {
		defer close(done)
		testBench(srv.URL).followShape(ctx, m, outcomes)
	}()
	<-done
	if got := <-outcomes; got != "admitted" {
		t.Fatalf("outcome %q, want admitted (err %v)", got, m.firstErr())
	}
	if err := m.firstErr(); err != nil {
		t.Fatalf("follower failed: %v", err)
	}
	if m.ok != 1 || m.atHead.Load() != 1 || m.retries.Load() != 1 || cursor.Load() != "100" {
		t.Fatalf("delivered %d, at head %d, retries %d, cursor %q", m.ok, m.atHead.Load(), m.retries.Load(), cursor.Load())
	}
}

// A metrics stub that serves the gauge value that gauge returns for each scrape.
func gaugeServer(t *testing.T, gauge func(scrape int64) int64) string {
	t.Helper()
	var scrapes atomic.Int64
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprintf(w, "# TYPE terrabase_shape_requests_waiting gauge\nterrabase_shape_requests_waiting %d\n", gauge(scrapes.Add(1)))
	}))
	t.Cleanup(srv.Close)
	return srv.URL
}

// Readiness reads the node's gauge, not the client's count: a gauge still rising is not ready, a settled gauge is, and a
// gauge that never rises within the timeout fails the step. Before a step the gauge must read zero.
func TestAwaitParkedReadsServerGauge(t *testing.T) {
	t.Parallel()
	t.Run("settled gauge is ready", func(t *testing.T) {
		t.Parallel()
		// Rising on scrape 2, then held: ready only after the settle window at 2.
		url := gaugeServer(t, func(scrape int64) int64 { return min(scrape-1, 2) })
		m := &measure{}
		m.atHead.Store(2)
		at, err := awaitParked(t.Context(), url, m, 2, 30*time.Second)
		if err != nil || at != 2 {
			t.Fatalf("ready at %d, err %v; want 2", at, err)
		}
	})
	t.Run("gauge that never rises times out", func(t *testing.T) {
		t.Parallel()
		url := gaugeServer(t, func(int64) int64 { return 0 })
		m := &measure{}
		m.atHead.Store(2)
		if _, err := awaitParked(t.Context(), url, m, 2, 300*time.Millisecond); err == nil {
			t.Fatal("ready with no parked requester, want a timeout")
		}
	})
	t.Run("pre-step needs zero", func(t *testing.T) {
		t.Parallel()
		if at, err := awaitParked(t.Context(), gaugeServer(t, func(int64) int64 { return 0 }), &measure{}, 0, time.Second); err != nil || at != 0 {
			t.Fatalf("pre-step at %d, err %v; want 0", at, err)
		}
		if _, err := awaitParked(t.Context(), gaugeServer(t, func(int64) int64 { return 1 }), &measure{}, 0, 300*time.Millisecond); err == nil {
			t.Fatal("pre-step ready with a parked requester left from an earlier step, want a timeout")
		}
	})
}

// Refusals before admission end the follower with its own outcome, a refusal the server does not answer with shape_busy
// or shape_too_large is an invalid measurement.
func TestFollowShapeRejectsBeforeAdmission(t *testing.T) {
	t.Parallel()
	cases := []struct {
		name, want string
		status     int
		code       string
		wantErr    bool
	}{
		{name: "busy", want: "rejected_shape_busy", status: http.StatusTooManyRequests, code: "shape_busy"},
		{name: "too large", want: "rejected_shape_too_large", status: http.StatusServiceUnavailable, code: "shape_too_large"},
		{name: "unavailable", want: "", status: http.StatusServiceUnavailable, code: "unavailable", wantErr: true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.WriteHeader(c.status)
				fmt.Fprintf(w, `{"error":{"code":%q,"message":"refused"}}`, c.code)
			}))
			defer srv.Close()
			m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3)}
			outcomes := make(chan string, 1)
			testBench(srv.URL).followShape(t.Context(), m, outcomes)
			if got := <-outcomes; got != c.want || (m.firstErr() != nil) != c.wantErr {
				t.Fatalf("outcome %q, err %v; want %q, error %t", got, m.firstErr(), c.want, c.wantErr)
			}
		})
	}
}
