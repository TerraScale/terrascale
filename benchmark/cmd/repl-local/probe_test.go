package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
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

func TestFeedsWaitForCursorAndJoinOnCancellation(t *testing.T) {
	t.Parallel()
	started := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		w.WriteHeader(http.StatusOK)
		w.(http.Flusher).Flush()
		close(started)
		<-r.Context().Done()
	}))
	defer server.Close()
	ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
	defer cancel()
	targets := replbench.Targets{Regions: map[string]replbench.RegionTarget{"gru": {URL: server.URL}}}
	done := make(chan error, 1)
	go func() {
		stop, admitted, rejected, err := openFeeds(ctx, targets, 1)
		if stop != nil {
			err = errors.Join(err, stop())
		}
		if admitted != 0 || rejected != 0 {
			err = errors.New("HTTP 200 without cursor was counted")
		}
		done <- err
	}()
	select {
	case <-started:
	case <-ctx.Done():
		t.Fatal("feed did not open")
	}
	select {
	case <-done:
		t.Fatal("feed admitted before cursor")
	default:
	}
	cancel()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("canceled admission succeeded")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("canceled feed did not join")
	}
}

func TestFeedsAdmitRealCapacityAndJoin(t *testing.T) {
	t.Parallel()
	var active, attempts atomic.Int64
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/changes") || r.URL.Query().Get("cursor") != "now" {
			t.Error("wrong feed endpoint")
		}
		attempts.Add(1)
		if active.Load() == 32 {
			w.WriteHeader(http.StatusTooManyRequests)
			fmt.Fprint(w, `{"error":{"code":"changes_busy"}}`)
			return
		}
		active.Add(1)
		defer active.Add(-1)
		w.Header().Set("Content-Type", "text/event-stream")
		fmt.Fprint(w, "event: cursor\ndata: {}\n\n")
		w.(http.Flusher).Flush()
		<-r.Context().Done()
	}))
	defer server.Close()
	targets := replbench.Targets{Project: ulid.Make().String(), Database: ulid.Make().String(), Regions: map[string]replbench.RegionTarget{"gru": {URL: server.URL}}}
	stop, admitted, rejected, err := openFeeds(t.Context(), targets, 100)
	if stop != nil {
		defer stop()
	}
	if err != nil || admitted != 32 || rejected != 68 || attempts.Load() != 100 || active.Load() != 32 {
		t.Fatalf("admission: %d admitted, %d rejected, %d attempts, %d active: %v", admitted, rejected, attempts.Load(), active.Load(), err)
	}
	if err := stop(); err != nil {
		t.Fatal(err)
	}
	if err := stop(); err != nil {
		t.Fatal(err)
	}
	// Server.Close joins handlers as well as the clients joined by stop.
	server.Close()
	if active.Load() != 0 {
		t.Fatal("feed handlers remain active after closure")
	}
}

func TestFeedsRejectFalseAdmissionAndRedactErrors(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"no-cursor", "unexpected-refusal", "error-event"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			token := "secret-token-must-not-appear"
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "text/event-stream")
				switch mode {
				case "unexpected-refusal":
					w.WriteHeader(http.StatusTooManyRequests)
					fmt.Fprintf(w, `{"error":{"code":"%s"}}`, token)
				case "error-event":
					fmt.Fprintf(w, "event: error\ndata: %s\n\n", token)
				case "no-cursor":
					fmt.Fprint(w, ": heartbeat\n\n")
				}
			}))
			defer server.Close()
			targets := replbench.Targets{Regions: map[string]replbench.RegionTarget{"gru": {URL: server.URL, Token: token}}}
			stop, admitted, rejected, err := openFeeds(t.Context(), targets, 1)
			if stop != nil {
				defer stop()
			}
			if err == nil || admitted != 0 || rejected != 0 {
				t.Fatalf("false admission: admitted=%d rejected=%d err=%v", admitted, rejected, err)
			}
			if strings.Contains(err.Error(), token) {
				t.Fatal("error disclosed token")
			}
		})
	}
}
