package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

func qosBusyFirst(n int32, next http.HandlerFunc) http.HandlerFunc {
	var seen atomic.Int32
	return func(w http.ResponseWriter, r *http.Request) {
		if seen.Add(1) <= n {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusTooManyRequests)
			fmt.Fprint(w, `{"error":{"code":"tenant_busy","message":"organization request budget exceeded"}}`)
			return
		}
		next(w, r)
	}
}

func TestQosWait(t *testing.T) {
	t.Parallel()
	for _, c := range []struct {
		retryAfter string
		want       time.Duration
	}{
		{retryAfter: "", want: shapeRetryWait},
		{retryAfter: "0", want: shapeRetryWait},
		{retryAfter: "junk", want: shapeRetryWait},
		{retryAfter: "1", want: time.Second},
	} {
		h := http.Header{}
		if c.retryAfter != "" {
			h.Set("Retry-After", c.retryAfter)
		}
		if got := qosWait(h); got != c.want {
			t.Errorf("Retry-After %q: wait %s, want %s", c.retryAfter, got, c.want)
		}
	}
}

func TestFollowShapeRepeatsQoSBusy(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	var refuseNext atomic.Bool
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		q := r.URL.Query()
		if refuseNext.CompareAndSwap(false, true) {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusTooManyRequests)
			fmt.Fprint(w, `{"error":{"code":"tenant_busy","message":"organization request budget exceeded"}}`)
			return
		}
		refuseNext.Store(false)
		w.Header().Set("Content-Type", "application/json")
		switch {
		case q.Get("live") == "true":
			cancel()
			fmt.Fprint(w, shapeReply("5", true, ""))
		case q.Get("offset") == "-1":
			w.Header().Set("TB-Shape-Handle", testHandle)
			w.Header().Set("Location", "/v1/projects/p/databases/d/collections/c/shape?handle="+testHandle+"&offset=snap.5.0")
			w.WriteHeader(http.StatusTemporaryRedirect)
		case q.Get("offset") == "snap.5.0":
			fmt.Fprint(w, shapeReply("5", true, ""))
		default:
			w.WriteHeader(http.StatusConflict)
			fmt.Fprint(w, `{"error":{"code":"must_refetch","message":"unexpected request","handle":"`+testHandle+`"}}`)
		}
	}))
	defer srv.Close()
	m := &measure{}
	outcomes := make(chan string, 1)
	testBench(srv.URL).followShape(ctx, m, outcomes)
	if got := <-outcomes; got != "admitted" {
		t.Fatalf("outcome %q, want admitted (err %v)", got, m.firstErr())
	}
	if m.firstErr() != nil || m.qosBusy.Load() != 3 || m.retries.Load() != 0 || m.atHead.Load() != 1 {
		t.Fatalf("err %v, qos busy %d, retries %d, at head %d; want no error, 3, 0, 1", m.firstErr(), m.qosBusy.Load(), m.retries.Load(), m.atHead.Load())
	}
}

func TestFollowChangesRepeatsQoSBusy(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	srv := httptest.NewServer(qosBusyFirst(1, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		fmt.Fprint(w, "event: cursor\ndata: {}\n\n")
		w.(http.Flusher).Flush()
		<-r.Context().Done()
	}))
	defer srv.Close()
	b := testBench(srv.URL)
	b.sse = &http.Client{}
	b.target = targets[0]
	m := &measure{}
	outcomes := make(chan string, 1)
	done := make(chan struct{})
	go func() {
		defer close(done)
		b.followChanges(ctx, m, outcomes)
	}()
	if got := <-outcomes; got != "admitted" {
		t.Fatalf("outcome %q, want admitted (err %v)", got, m.firstErr())
	}
	cancel()
	<-done
	if m.firstErr() != nil || m.qosBusy.Load() != 1 {
		t.Fatalf("err %v, qos busy %d; want no error and 1", m.firstErr(), m.qosBusy.Load())
	}
}

func TestPutRepeatsQoSBusy(t *testing.T) {
	t.Parallel()
	const doc = `{"t":1,"pad":"x"}`
	srv := httptest.NewServer(qosBusyFirst(2, func(w http.ResponseWriter, r *http.Request) {
		if body, _ := io.ReadAll(r.Body); string(body) != doc {
			t.Errorf("repeat sent %q, want %q", body, doc)
		}
		w.WriteHeader(http.StatusCreated)
	}))
	defer srv.Close()
	w, err := benchclient.NewRegion("gru", benchclient.Targets{Project: "p", Database: "d", Regions: map[string]benchclient.RegionTarget{
		"gru": {URL: srv.URL, Token: "t"},
	}}, 1)
	if err != nil {
		t.Fatal(err)
	}
	b := &bench{writer: w}
	var qos atomic.Int64
	resp, err := b.put(t.Context(), benchclient.DocPath("c", "01A"), []byte(doc), &qos)
	if err != nil || resp.Status != http.StatusCreated || qos.Load() != 2 {
		t.Fatalf("status %d, err %v, qos busy %d; want 201, no error, 2", resp.Status, err, qos.Load())
	}
}
