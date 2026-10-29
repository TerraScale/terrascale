package v1_test

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

const (
	metricsProject      = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	metricsDatabase     = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	metricsHeader       = `{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","range":"1h","from":"2026-10-08T11:00:00Z","to":"2026-10-08T12:00:00Z","bucketSeconds":60,`
	metricsEmptyFixture = metricsHeader + `"items":[]}`
	analyticsFixture    = metricsHeader + `"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"main","region":"gru","series":[{"start":"2026-10-08T11:00:00Z","requests":{"get":12},"operations":{"read":9007199254740993},"errors":2,"latency":{"p50Ms":0,"p95Ms":2.5,"p99Ms":null},"storageBytes":null,"clientBandwidth":{"bytesSent":9007199254740993,"bytesReceived":20},"replicationBandwidth":{"bytesSent":30,"bytesReceived":40}}]}]}`
	replicationFixture  = metricsHeader + `"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"main","region":"gru","status":"healthy","replicatedOps":9007199254740993,"lagMs":0,"divergence":false,"convergenceMs":12.5,"lastApplied":"2026-10-08T11:59:59.123Z","bandwidth":[{"sourceRegion":"gru","destinationRegion":"sfo","kind":"delta","bytesSent":9007199254740993,"bytesReceived":200}],"series":[{"start":"2026-10-08T11:00:00Z","replicatedOps":3,"bandwidth":[]}]},{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"main","region":"sfo","status":"future-status","replicatedOps":0,"lagMs":null,"divergence":null,"convergenceMs":null,"lastApplied":null,"bandwidth":[],"series":[]}]}`
)

func metricsUser(t *testing.T, server *httptest.Server, retries int) *tb.ManagementUser {
	t.Helper()
	m, err := tb.NewManagement(tb.Config{BaseURL: server.URL + "/proxy", HTTPClient: server.Client(), MaxRetries: retries})
	if err != nil {
		t.Fatal(err)
	}
	return m.WithToken("project-access-token")
}

func TestMetricsActualMainContracts(t *testing.T) {
	for _, endpoint := range []string{"analytics", "replication"} {
		for _, window := range []tb.MetricsRange{tb.Range1Hour, tb.Range24Hours, tb.Range7Days, tb.Range30Days} {
			t.Run(endpoint+"/"+string(window), func(t *testing.T) {
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					if r.Method != http.MethodGet || r.URL.Path != "/proxy/v1/projects/"+metricsProject+"/"+endpoint || r.Header.Get("Authorization") != "Bearer project-access-token" || r.Header.Get("X-Terrabase-User-Id") != "" {
						t.Errorf("request: %s %s", r.Method, r.URL)
					}
					q := r.URL.Query()
					if q.Get("range") != string(window) || q.Get("database") != metricsDatabase || q.Get("region") != "gru" || len(q) != 3 {
						t.Errorf("query: %v", q)
					}
					if endpoint == "analytics" {
						io.WriteString(w, analyticsFixture)
					} else {
						io.WriteString(w, replicationFixture)
					}
				}))
				defer server.Close()
				u := metricsUser(t, server, -1)
				opts := tb.MetricsOptions{Range: window, DatabaseID: metricsDatabase, Region: "gru"}
				if endpoint == "analytics" {
					out, err := u.Analytics(t.Context(), metricsProject, opts)
					if err != nil {
						t.Fatal(err)
					}
					item := out.Items[0]
					point := item.Series[0]
					if out.Range != tb.Range1Hour || out.BucketSeconds != 60 || item.DatabaseName != "main" || item.Region != "gru" || point.Operations["read"] != 9007199254740993 || point.Latency.P50MS == nil || *point.Latency.P50MS != 0 || *point.Latency.P95MS != 2.5 || point.Latency.P99MS != nil || point.StorageBytes != nil || point.ClientBandwidth.BytesSent != 9007199254740993 || point.ReplicationBandwidth.BytesReceived != 40 {
						t.Fatalf("analytics: %+v %+v", item, point)
					}
				} else {
					out, err := u.Replication(t.Context(), metricsProject, opts)
					if err != nil {
						t.Fatal(err)
					}
					item := out.Items[0]
					missing := out.Items[1]
					if item.DatabaseName != "main" || item.ReplicatedOps != 9007199254740993 || item.Divergence == nil || *item.Divergence || item.LagMS == nil || *item.LagMS != 0 || item.ConvergenceMS == nil || *item.ConvergenceMS != 12.5 || item.Bandwidth[0].Kind != "delta" || item.Bandwidth[0].BytesSent != 9007199254740993 || item.Series[0].ReplicatedOps != 3 || missing.Divergence != nil || missing.ConvergenceMS != nil || missing.Status != "future-status" {
						t.Fatalf("replication: %+v %+v", item, missing)
					}
				}
			})
		}
	}
}

func TestMetricsRejectMalformedMainResponses(t *testing.T) {
	for _, fixture := range []string{analyticsFixture, replicationFixture} {
		bodies := []string{`null`, `{}`, strings.Replace(fixture, `"items":[`, `"items":null,"ignored":[`, 1), strings.Replace(fixture, `"series":[`, `"series":null,"ignored":[`, 1), strings.Replace(fixture, `"bucketSeconds":60`, `"bucketSeconds":0`, 1), strings.Replace(fixture, `"databaseName":"main"`, `"databaseName":""`, 1)}
		if fixture == analyticsFixture {
			bodies = append(bodies, strings.Replace(fixture, `"latency":{"p50Ms":0,"p95Ms":2.5,"p99Ms":null}`, `"latency":null`, 1))
		} else {
			bodies = append(bodies, strings.Replace(fixture, `"divergence":false`, `"divergence":0`, 1))
		}
		for _, body := range bodies {
			transport := &scriptTransport{responses: []scripted{{status: 200, body: body}}}
			m, err := tb.NewManagement(tb.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: transport}, MaxRetries: -1})
			if err != nil {
				t.Fatal(err)
			}
			if fixture == analyticsFixture {
				_, err = m.WithToken("project-access-token").Analytics(t.Context(), metricsProject, tb.MetricsOptions{})
			} else {
				_, err = m.WithToken("project-access-token").Replication(t.Context(), metricsProject, tb.MetricsOptions{})
			}
			if err == nil {
				t.Fatalf("accepted malformed metrics: %s", body)
			}
		}
	}
}

func TestMetricsRejectInvalidFilters(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		io.WriteString(w, metricsEmptyFixture)
	}))
	t.Cleanup(server.Close)
	u := metricsUser(t, server, -1)
	for _, test := range []struct {
		project string
		opts    tb.MetricsOptions
	}{
		{"", tb.MetricsOptions{}}, {"0190a6e4-7c3b-7d2e-8f00-0123456789ab", tb.MetricsOptions{}}, {"proj_" + metricsProject, tb.MetricsOptions{}}, {"../" + metricsProject, tb.MetricsOptions{}}, {"81ARZ3NDEKTSV4RRFFQ69G5FAV", tb.MetricsOptions{}},
		{metricsProject, tb.MetricsOptions{DatabaseID: "main"}}, {metricsProject, tb.MetricsOptions{DatabaseID: "0190a6e4-7c3b-7d2e-8f00-0123456789ab"}}, {metricsProject, tb.MetricsOptions{Range: "2h"}},
		{"01arz3ndektsv4rrffq69g5fav", tb.MetricsOptions{}}, {metricsProject, tb.MetricsOptions{DatabaseID: "01arz3ndektsv4rrffq69g5faw"}},
		{"01aRz3NdEkTsV4rRfFq69G5fAv", tb.MetricsOptions{}}, {metricsProject, tb.MetricsOptions{DatabaseID: "01ArZ3nDeKtSv4RrFfQ69g5FaW"}},
		{"01arz3ndektsv4rrffq69g5fai", tb.MetricsOptions{}}, {metricsProject, tb.MetricsOptions{DatabaseID: "01arz3ndektsv4rrffq69g5fau"}},
		{"01arz3ndektsv4rrffq69g5fal", tb.MetricsOptions{}}, {metricsProject, tb.MetricsOptions{DatabaseID: "01arz3ndektsv4rrffq69g5fao"}},
		{"01ARZ3NDEKTSV4RRFFQ69G5FAV", tb.MetricsOptions{}}, {metricsProject, tb.MetricsOptions{DatabaseID: "01ARZ3NDEKTſV4RRFFQ69G5FAW"}},
	} {
		if _, err := u.Analytics(t.Context(), test.project, test.opts); err == nil {
			t.Fatalf("analytics accepted %+v", test)
		}
		if _, err := u.Replication(t.Context(), test.project, test.opts); err == nil {
			t.Fatalf("replication accepted %+v", test)
		}
	}
	m, err := tb.NewManagement(tb.Config{BaseURL: server.URL, Token: testCredential})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := m.WithToken("").Analytics(t.Context(), metricsProject, tb.MetricsOptions{}); err == nil {
		t.Fatal("empty token accepted")
	}
	if _, err := m.WithToken("bad token").Replication(t.Context(), metricsProject, tb.MetricsOptions{}); err == nil {
		t.Fatal("invalid token accepted")
	}
	if _, err := m.As("user-id").Analytics(t.Context(), metricsProject, tb.MetricsOptions{}); err == nil {
		t.Fatal("administrative credential accepted for project-token analytics")
	}
	if _, err := m.As("user-id").Replication(t.Context(), metricsProject, tb.MetricsOptions{}); err == nil {
		t.Fatal("administrative credential accepted for project-token replication")
	}
	if calls.Load() != 0 {
		t.Fatalf("invalid filters sent %d requests", calls.Load())
	}
}

func TestMetricsRejectInvalidResponseIDs(t *testing.T) {
	for _, id := range []string{"", "proj_" + metricsProject, "0190a6e4-7c3b-7d2e-8f00-0123456789ab", strings.ToLower(metricsProject), "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAI"} {
		for _, fixture := range []string{analyticsFixture, replicationFixture} {
			for _, entity := range []string{metricsProject, metricsDatabase} {
				body := strings.ReplaceAll(fixture, entity, id)
				transport := &scriptTransport{responses: []scripted{{status: http.StatusOK, body: body}}}
				m, err := tb.NewManagement(tb.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: transport}})
				if err != nil {
					t.Fatal(err)
				}
				u := m.WithToken("project-access-token")
				if fixture == analyticsFixture {
					_, err = u.Analytics(t.Context(), metricsProject, tb.MetricsOptions{})
				} else {
					_, err = u.Replication(t.Context(), metricsProject, tb.MetricsOptions{})
				}
				if err == nil {
					t.Fatal("accepted a noncanonical metrics response ID")
				}
			}
		}
	}
}

func TestMetricsErrorsAndRetries(t *testing.T) {
	t.Parallel()
	for _, endpoint := range []string{"analytics", "replication"} {
		for _, test := range []struct {
			name      string
			status    int
			body      string
			retries   int
			wantCalls int
			wantCode  tb.ErrorCode
		}{
			{"forbidden", 403, `{"error":{"code":"forbidden_scope","message":"denied"}}`, 1, 1, tb.ErrForbiddenScope},
			{"not found", 404, `{"error":{"code":"not_found","message":"absent"}}`, 1, 1, tb.ErrNotFound},
			{"transient", 503, `{"error":{"code":"capacity","message":"busy"}}`, 1, 2, ""},
			{"transient confirmation", 503, `{"error":{"code":"confirmation_busy","message":"busy"}}`, 1, 2, ""},
			{"copy faulted", 503, `{"error":{"code":"copy_faulted","message":"recovering"}}`, 1, 1, tb.ErrCopyFaulted},
			{"malformed", 200, `{"series":`, -1, 1, ""},
		} {
			t.Run(endpoint+"/"+test.name, func(t *testing.T) {
				t.Parallel()
				var calls atomic.Int32
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					n := calls.Add(1)
					w.Header().Set("Content-Type", "application/json")
					if test.wantCalls == 2 && n == 2 {
						io.WriteString(w, metricsEmptyFixture)
						return
					}
					w.WriteHeader(test.status)
					io.WriteString(w, test.body)
				}))
				t.Cleanup(server.Close)
				u := metricsUser(t, server, test.retries)
				var err error
				if endpoint == "analytics" {
					_, err = u.Analytics(t.Context(), metricsProject, tb.MetricsOptions{})
				} else {
					_, err = u.Replication(t.Context(), metricsProject, tb.MetricsOptions{})
				}
				if test.wantCalls == 2 {
					if err != nil {
						t.Fatal(err)
					}
				} else if err == nil {
					t.Fatal("failure accepted")
				}
				if test.wantCode != "" {
					var apiErr *tb.APIError
					if !errors.Is(err, test.wantCode) || !errors.As(err, &apiErr) || apiErr.Status != test.status {
						t.Fatalf("error = %v", err)
					}
				}
				if calls.Load() != int32(test.wantCalls) {
					t.Fatalf("calls = %d", calls.Load())
				}
			})
		}
	}
}

func TestMetricsCancellation(t *testing.T) {
	t.Parallel()
	for _, endpoint := range []string{"analytics", "replication"} {
		t.Run(endpoint, func(t *testing.T) {
			t.Parallel()
			started, release := make(chan struct{}), make(chan struct{})
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { close(started); <-release }))
			t.Cleanup(server.Close)
			defer close(release)
			u := metricsUser(t, server, -1)
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			done := make(chan error, 1)
			go func() {
				var err error
				if endpoint == "analytics" {
					_, err = u.Analytics(ctx, metricsProject, tb.MetricsOptions{})
				} else {
					_, err = u.Replication(ctx, metricsProject, tb.MetricsOptions{})
				}
				done <- err
			}()
			select {
			case <-started:
			case <-ctx.Done():
				t.Fatal("request did not start")
			}
			cancel()
			select {
			case err := <-done:
				if !errors.Is(err, context.Canceled) {
					t.Fatalf("error = %v", err)
				}
			case <-time.After(5 * time.Second):
				t.Fatal("request ignored cancellation")
			}
		})
	}
}
