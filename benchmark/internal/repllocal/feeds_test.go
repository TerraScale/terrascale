package repllocal

import (
	"context"
	"errors"
	"fmt"
	"github.com/oklog/ulid/v2"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"terrascale.tech/ts-benchmark/internal/replbench"
	"testing"
	"time"
)

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
		stop, admitted, rejected, err := OpenFeeds(ctx, targets, 1)
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
	stop, admitted, rejected, err := OpenFeeds(t.Context(), targets, 100)
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
			stop, admitted, rejected, err := OpenFeeds(t.Context(), targets, 1)
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
