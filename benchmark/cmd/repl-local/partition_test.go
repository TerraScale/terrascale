package main

import (
	"context"
	"errors"
	"fmt"
	"github.com/oklog/ulid/v2"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"terrascale.tech/ts-benchmark/internal/repllocal"
	"testing"
	"time"

	"terrascale.tech/ts-benchmark/internal/replbench"
)

func TestPartitionOutcomeRejectsFalseSuccessAndRetryAmbiguity(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, body   string
		status       int
		retry, valid bool
	}{
		{"unknown", `{"error":{"code":"write_outcome_unknown"}}`, 503, false, true},
		{"success-status", `{"error":{"code":"write_outcome_unknown"}}`, 201, false, false},
		{"unsafe-retry", `{"error":{"code":"write_outcome_unknown"}}`, 503, true, false},
		{"false-durability", `{"error":{"code":"write_outcome_unknown"},"durability":"remote_durable"}`, 503, false, false},
		{"wrong-code", `{"error":{"code":"remote_region_unavailable"}}`, 503, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			err := requireOutcome(replbench.Response{Status: tc.status, Body: []byte(tc.body), HasRetryAfter: tc.retry}, 503, "write_outcome_unknown", false)
			if (err == nil) != tc.valid {
				t.Fatalf("valid=%t: %v", tc.valid, err)
			}
		})
	}
}

func TestPartitionWaitsForCollectionBeforeSingleSendWrites(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"ready", "terminal", "deadline"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			ctx, cancel := context.WithTimeout(t.Context(), time.Second)
			defer cancel()
			if mode == "deadline" {
				var deadline context.CancelFunc
				ctx, deadline = context.WithTimeout(ctx, 100*time.Millisecond)
				defer deadline()
			}
			var reads [2]atomic.Int32
			var puts atomic.Int32
			c := &repllocal.Cluster{Nodes: map[string]*repllocal.Worker{}}
			project, database := ulid.Make().String(), ulid.Make().String()
			for i, name := range []string{"gru", "sjc"} {
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					switch r.Method {
					case http.MethodPost:
						w.WriteHeader(http.StatusCreated)
					case http.MethodGet:
						if !strings.Contains(r.URL.Path, "/collections/proof-") {
							t.Errorf("unexpected readiness path %s", r.URL.Path)
						}
						attempt := reads[i].Add(1)
						switch {
						case mode == "terminal":
							w.WriteHeader(http.StatusForbidden)
						case mode == "deadline" || attempt == 1:
							w.WriteHeader(http.StatusNotFound)
						case attempt == 2:
							w.WriteHeader(http.StatusServiceUnavailable)
						default:
							fmt.Fprint(w, `{"name":"collection"}`)
						}
					case http.MethodPut:
						puts.Add(1)
						if reads[0].Load() < 3 || reads[1].Load() < 3 {
							t.Error("proof PUT sent before every region discovered the collection")
						}
						// Stop the proof after its first healthy PUT, before peer/ACK instrumentation.
						w.WriteHeader(http.StatusBadRequest)
						fmt.Fprint(w, `{"error":{"code":"test_stop"}}`)
					}
				}))
				defer server.Close()
				node := &repllocal.Worker{Target: repllocal.WorkerTarget{URL: server.URL, Database: database}}
				node.Config.Project = project
				c.Nodes[name] = node
			}
			err := partitionProof(ctx, c, map[string]float64{})
			if err == nil {
				t.Fatal("expected proof to stop")
			}
			switch mode {
			case "ready":
				if puts.Load() != 1 || !strings.Contains(err.Error(), "HTTP 400") {
					t.Fatalf("healthy PUT must be single-send after readiness: puts=%d err=%v", puts.Load(), err)
				}
			case "terminal":
				if puts.Load() != 0 || reads[0].Load()+reads[1].Load() != 1 || !strings.Contains(err.Error(), "HTTP 403") {
					t.Fatalf("terminal readiness must stop immediately: puts=%d err=%v", puts.Load(), err)
				}
			case "deadline":
				if puts.Load() != 0 || !errors.Is(err, context.DeadlineExceeded) {
					t.Fatalf("readiness must stop at parent deadline: puts=%d err=%v", puts.Load(), err)
				}
			}
		})
	}
}
