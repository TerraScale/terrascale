package repllocal

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/oklog/ulid/v2"
	"net/http"
	"net/http/httptest"
	"sync"
	"terrascale.tech/ts-benchmark/internal/replbench"
	"testing"
	"time"
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
					w.WriteHeader(http.StatusServiceUnavailable)
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
