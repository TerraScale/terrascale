package v1_test

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

const (
	metricsProject   = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	metricsDatabase  = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	analyticsFixture = `{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","from":"2026-10-08T11:00:00Z","to":"2026-10-08T12:00:00Z","bucketSeconds":60,"next":"opaque+/=&","series":[
	{"start":"2026-10-08T11:00:00Z","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","region":"gru","requests":{"get":12,"query":5},"ops":{"read":30,"write":3},"errors":2,"p50LatencyMs":0,"p95LatencyMs":2.5,"p99LatencyMs":7.25,"storageBytes":9007199254740993,"clientBytesSent":100,"clientBytesReceived":200,"replicationBandwidth":[
	{"sourceRegion":"gru","destinationRegion":"sfo","bytesSent":9007199254740993,"bytesReceived":20,"meter":"replication_bytes"},
	{"sourceRegion":"sfo","destinationRegion":"gru","bytesSent":30,"bytesReceived":40,"meter":"replication_bytes"}]},
	{"start":"2026-10-08T11:01:00Z","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","region":"sfo","p50LatencyMs":null,"p95LatencyMs":null,"p99LatencyMs":null}]}`
	replicationFixture = `{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","from":"2026-10-08T11:00:00Z","to":"2026-10-08T12:00:00Z","bucketSeconds":60,"next":"opaque+/=&","items":[
	{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","region":"gru","replicatedOps":9007199254740993,"bandwidth":[{"sourceRegion":"gru","destinationRegion":"sfo","bytesSent":100,"bytesReceived":200,"meter":"replication_bytes"}],"lagMs":0,"divergence":0,"convergenceTimeMs":12.5,"lastApplied":"2026-10-08T11:59:59.123Z","status":"healthy"},
	{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","region":"sfo","lagMs":null,"divergence":null,"convergenceTimeMs":null,"lastApplied":null,"status":"stopped"},
	{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","status":"lagging"},{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","status":"diverged"},{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","status":"new-status"}]}`
)

func metricsUser(t *testing.T, server *httptest.Server, retries int) *tb.ManagementUser {
	t.Helper()
	m, err := tb.NewManagement(tb.Config{BaseURL: server.URL + "/proxy", HTTPClient: server.Client(), MaxRetries: retries})
	if err != nil {
		t.Fatal(err)
	}
	return m.WithToken("project-access-token")
}

func TestMetricsAcceptCanonicalULIDs(t *testing.T) {
	t.Parallel()
	for _, ids := range []struct{ project, database string }{
		{metricsProject, metricsDatabase},
	} {
		t.Run(ids.project, func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet || (r.URL.Path != "/proxy/projects/"+ids.project+"/analytics" && r.URL.Path != "/proxy/projects/"+ids.project+"/replication") || r.URL.Query().Get("database") != ids.database {
					t.Errorf("ULIDs changed in request: %s %s", r.Method, r.URL)
				}
				calls.Add(1)
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, `{"projectId":"`+metricsProject+`"}`)
			}))
			t.Cleanup(server.Close)
			u := metricsUser(t, server, -1)
			opts := tb.MetricsOptions{DatabaseID: ids.database}
			if _, err := u.Analytics(t.Context(), ids.project, opts); err != nil {
				t.Fatal(err)
			}
			if _, err := u.Replication(t.Context(), ids.project, opts); err != nil {
				t.Fatal(err)
			}
			if calls.Load() != 2 {
				t.Fatalf("calls = %d, want both APIs", calls.Load())
			}
		})
	}
}

func TestMetricsFiltersAndPaging(t *testing.T) {
	t.Parallel()
	for _, endpoint := range []string{"analytics", "replication"} {
		for _, window := range []tb.MetricsRange{"", tb.Range1Hour, tb.Range24Hours, tb.Range7Days, tb.Range30Days} {
			t.Run(endpoint+"/"+string(window), func(t *testing.T) {
				t.Parallel()
				var calls atomic.Int32
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					page := calls.Add(1)
					want := url.Values{"database": {metricsDatabase}, "region": {"gru&other=sfo"}, "limit": {"100"}}
					if window != "" {
						want.Set("range", string(window))
					}
					if page > 1 {
						want.Set("after", "opaque+/=&")
					}
					if r.Method != "GET" || r.URL.Path != "/proxy/projects/"+metricsProject+"/"+endpoint || !reflect.DeepEqual(r.URL.Query(), want) {
						t.Errorf("request = %s %s; query want %v", r.Method, r.URL, want)
					}
					if r.Header.Get("Authorization") != "Bearer project-access-token" || r.Header.Get("X-TerraBase-User-Id") != "" {
						t.Error("project-token authorization missing or administrative identity present")
					}
					w.Header().Set("Content-Type", "application/json")
					if page == 1 {
						io.WriteString(w, `{"projectId":"`+metricsProject+`","next":"opaque+/=&"}`)
					} else {
						io.WriteString(w, `{"projectId":"`+metricsProject+`","next":""}`)
					}
				}))
				t.Cleanup(server.Close)
				u := metricsUser(t, server, -1)
				opts := tb.MetricsOptions{Range: window, DatabaseID: metricsDatabase, Region: "gru&other=sfo", Limit: 100}
				for page := range 2 {
					var next string
					if endpoint == "analytics" {
						out, err := u.Analytics(t.Context(), metricsProject, opts)
						if err != nil {
							t.Fatal(err)
						}
						next = out.Next
					} else {
						out, err := u.Replication(t.Context(), metricsProject, opts)
						if err != nil {
							t.Fatal(err)
						}
						next = out.Next
					}
					if (page == 0 && next != "opaque+/=&") || (page == 1 && next != "") {
						t.Fatalf("page %d next = %q", page, next)
					}
					opts.After = next
				}
				if calls.Load() != 2 {
					t.Fatalf("calls = %d", calls.Load())
				}
			})
		}
	}
}

func TestMetricsDecoding(t *testing.T) {
	t.Parallel()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.RawQuery != "" {
			t.Errorf("default options sent query %q", r.URL.RawQuery)
		}
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/proxy/projects/"+metricsProject+"/analytics" {
			io.WriteString(w, analyticsFixture)
		} else {
			io.WriteString(w, replicationFixture)
		}
	}))
	t.Cleanup(server.Close)
	u := metricsUser(t, server, -1)
	a, err := u.Analytics(t.Context(), metricsProject, tb.MetricsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if a.ProjectID != metricsProject || a.From.Format(time.RFC3339) != "2026-10-08T11:00:00Z" || a.To.Sub(a.From) != time.Hour || a.BucketSeconds != 60 || len(a.Series) != 2 {
		t.Fatalf("analytics = %+v", a)
	}
	p := a.Series[0]
	if p.Start != a.From || p.DatabaseID != metricsDatabase || p.Region != "gru" || p.Requests["get"] != 12 || p.Requests["query"] != 5 || p.Ops["read"] != 30 || p.Ops["write"] != 3 || p.Errors != 2 || p.StorageBytes != 9007199254740993 || p.ClientBytesSent != 100 || p.ClientBytesReceived != 200 {
		t.Fatalf("point = %+v", p)
	}
	if p.P50LatencyMS == nil || *p.P50LatencyMS != 0 || p.P95LatencyMS == nil || *p.P95LatencyMS != 2.5 || p.P99LatencyMS == nil || *p.P99LatencyMS != 7.25 || a.Series[1].P50LatencyMS != nil || a.Series[1].P95LatencyMS != nil || a.Series[1].P99LatencyMS != nil {
		t.Fatal("latencies lost zero, fractional or missing measurements")
	}
	wantBandwidth := []tb.ReplicationBandwidth{{SourceRegion: "gru", DestinationRegion: "sfo", BytesSent: 9007199254740993, BytesReceived: 20, Meter: tb.ReplicationBytesMeter}, {SourceRegion: "sfo", DestinationRegion: "gru", BytesSent: 30, BytesReceived: 40, Meter: tb.ReplicationBytesMeter}}
	if !reflect.DeepEqual(p.ReplicationBandwidth, wantBandwidth) {
		t.Fatalf("bandwidth = %+v", p.ReplicationBandwidth)
	}
	r, err := u.Replication(t.Context(), metricsProject, tb.MetricsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if r.ProjectID != metricsProject || r.From != a.From || r.To != a.To || r.BucketSeconds != 60 || len(r.Items) != 5 {
		t.Fatalf("replication = %+v", r)
	}
	m := r.Items[0]
	if m.DatabaseID != metricsDatabase || m.Region != "gru" || m.ReplicatedOps != 9007199254740993 || m.LagMS == nil || *m.LagMS != 0 || m.Divergence == nil || *m.Divergence != 0 || m.ConvergenceTimeMS == nil || *m.ConvergenceTimeMS != 12.5 || m.LastApplied == nil || m.LastApplied.Format(time.RFC3339Nano) != "2026-10-08T11:59:59.123Z" {
		t.Fatalf("replica = %+v", m)
	}
	if !reflect.DeepEqual(m.Bandwidth, []tb.ReplicationBandwidth{{SourceRegion: "gru", DestinationRegion: "sfo", BytesSent: 100, BytesReceived: 200, Meter: tb.ReplicationBytesMeter}}) {
		t.Fatalf("replica bandwidth = %+v", m.Bandwidth)
	}
	missing := r.Items[1]
	if missing.LagMS != nil || missing.Divergence != nil || missing.ConvergenceTimeMS != nil || missing.LastApplied != nil {
		t.Fatal("missing replication observations became zeroes")
	}
	for i, status := range []tb.ReplicationStatus{tb.ReplicationHealthy, tb.ReplicationStopped, tb.ReplicationLagging, tb.ReplicationDiverged, "new-status"} {
		if r.Items[i].Status != status {
			t.Fatalf("status = %q, want %q", r.Items[i].Status, status)
		}
	}
}

func TestMetricsRejectInvalidFilters(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		io.WriteString(w, `{"projectId":"`+metricsProject+`"}`)
	}))
	t.Cleanup(server.Close)
	u := metricsUser(t, server, -1)
	for _, test := range []struct {
		project string
		opts    tb.MetricsOptions
	}{
		{"", tb.MetricsOptions{}}, {"0190a6e4-7c3b-7d2e-8f00-0123456789ab", tb.MetricsOptions{}}, {"proj_" + metricsProject, tb.MetricsOptions{}}, {"../" + metricsProject, tb.MetricsOptions{}}, {"81ARZ3NDEKTSV4RRFFQ69G5FAV", tb.MetricsOptions{}},
		{metricsProject, tb.MetricsOptions{DatabaseID: "main"}}, {metricsProject, tb.MetricsOptions{DatabaseID: "0190a6e4-7c3b-7d2e-8f00-0123456789ab"}}, {metricsProject, tb.MetricsOptions{Range: "2h"}}, {metricsProject, tb.MetricsOptions{Limit: -1}}, {metricsProject, tb.MetricsOptions{Limit: 101}},
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
			{"malformed", 200, `{"series":`, -1, 1, ""},
		} {
			t.Run(endpoint+"/"+test.name, func(t *testing.T) {
				t.Parallel()
				var calls atomic.Int32
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					n := calls.Add(1)
					w.Header().Set("Content-Type", "application/json")
					if test.name == "transient" && n == 2 {
						io.WriteString(w, `{"projectId":"`+metricsProject+`"}`)
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
				if test.name == "transient" {
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
