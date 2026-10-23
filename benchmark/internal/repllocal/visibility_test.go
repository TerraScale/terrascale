package repllocal

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

func TestVisibilityPollsBeforeACKAndRequiresSuccessfulMatchingWrite(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"visible-before-ack", "wrong-body", "failed-write"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			written, observed, ack := make(chan struct{}), make(chan struct{}), make(chan struct{})
			var release sync.Once
			var body []byte
			origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				var doc map[string]string
				if json.NewDecoder(r.Body).Decode(&doc) != nil {
					t.Error("invalid PUT body")
					return
				}
				if _, err := ulid.ParseStrict(r.URL.Query().Get("id")); err != nil {
					t.Error("document ID is not a plain ULID")
				}
				body, _ = json.Marshal(doc)
				close(written)
				select {
				case <-ack:
				case <-r.Context().Done():
					return
				}
				if mode == "failed-write" {
					w.WriteHeader(http.StatusBadRequest)
				} else {
					w.WriteHeader(http.StatusOK)
				}
			}))
			defer origin.Close()
			replica := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				select {
				case <-written:
				case <-r.Context().Done():
					return
				}
				if mode == "wrong-body" {
					fmt.Fprint(w, `{"document":{"probe":"wrong"}}`)
				} else {
					fmt.Fprintf(w, `{"document":%s}`, body)
				}
				close(observed)
			}))
			defer replica.Close()
			defer release.Do(func() { close(ack) })
			targets := replbench.Targets{Project: ulid.Make().String(), Database: ulid.Make().String(), Regions: map[string]replbench.RegionTarget{
				"gru": {URL: origin.URL}, "sjc": {URL: replica.URL},
			}}
			regions := make(map[string]*replbench.Region)
			for name := range targets.Regions {
				r, err := replbench.NewRegion(name, targets, 2)
				if err != nil {
					t.Fatal(err)
				}
				regions[name] = r
			}
			hist := map[string]*replbench.Latency{"gru->sjc": replbench.NewLatency()}
			done := make(chan error, 1)
			go func() { done <- visibilityProbe(ctx, regions["gru"], regions, ulid.Make().String(), hist) }()
			select {
			case <-observed:
			case <-ctx.Done():
				t.Fatal("replica was not polled while PUT awaited ACK")
			}
			if hist["gru->sjc"].Summary().Count != 0 {
				t.Fatal("recorded a sample before successful PUT")
			}
			release.Do(func() { close(ack) })
			select {
			case err := <-done:
				if mode == "visible-before-ack" {
					if err != nil || hist["gru->sjc"].Summary().Count != 1 {
						t.Fatalf("successful pre-ACK visibility: histogram=%+v err=%v", hist["gru->sjc"].Summary(), err)
					}
				} else if err == nil || hist["gru->sjc"].Summary().Count != 0 {
					t.Fatalf("invalid measurement accepted: %v", err)
				}
			case <-ctx.Done():
				t.Fatal("probe failed to join")
			}
		})
	}
}

func TestProbeRequestRetries503WithSameDocumentAndHonorsDeadline(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"put-retry", "get-retry", "persistent-503", "terminal-400"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			id, body := ulid.Make().String(), `{"value":1}`
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				attempt := calls.Add(1)
				if r.URL.Query().Get("id") != id {
					t.Error("retry changed document ID")
				}
				if mode == "put-retry" {
					got, err := io.ReadAll(r.Body)
					if err != nil || string(got) != body || r.Method != http.MethodPut {
						t.Error("retry changed PUT body or method")
					}
				}
				switch {
				case mode == "terminal-400":
					w.WriteHeader(http.StatusBadRequest)
				case mode == "persistent-503" || attempt <= 2:
					w.WriteHeader(http.StatusServiceUnavailable)
				default:
					w.WriteHeader(http.StatusOK)
				}
			}))
			defer server.Close()
			targets := replbench.Targets{Project: ulid.Make().String(), Database: ulid.Make().String(), Regions: map[string]replbench.RegionTarget{"gru": {URL: server.URL}}}
			region, err := replbench.NewRegion("gru", targets, 1)
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			if mode == "persistent-503" {
				var deadline context.CancelFunc
				ctx, deadline = context.WithTimeout(ctx, 250*time.Millisecond)
				defer deadline()
			}
			method := http.MethodGet
			if mode == "put-retry" {
				method = http.MethodPut
			}
			resp, err := probeRequest(ctx, region, method, replbench.DocPath("items", id), []byte(body), "application/json")
			switch mode {
			case "persistent-503":
				if !errors.Is(err, context.DeadlineExceeded) {
					t.Fatalf("persistent 503 did not stop at deadline: %v", err)
				}
			case "terminal-400":
				if err != nil || resp.Status != http.StatusBadRequest || calls.Load() != 1 {
					t.Fatalf("terminal response retried: calls=%d status=%d err=%v", calls.Load(), resp.Status, err)
				}
			default:
				if err != nil || resp.Status != http.StatusOK || calls.Load() != 3 {
					t.Fatalf("transient 503 retry: calls=%d status=%d err=%v", calls.Load(), resp.Status, err)
				}
			}
		})
	}
}
