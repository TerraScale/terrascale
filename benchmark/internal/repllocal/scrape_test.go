package repllocal

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const runtimeMetrics = `# TYPE dotnet_process_cpu_time_seconds_total counter
dotnet_process_cpu_time_seconds_total{cpu_mode="user",otel_scope_name="System.Runtime"} 1.25 1234
dotnet_process_cpu_time_seconds_total{cpu_mode="system",otel_scope_name="System.Runtime"} 0.5
dotnet_gc_heap_total_allocated_bytes_total 4096
`

func TestScrapeMetrics(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, body, wantErr string
		feed, merged        float64
	}{
		{name: "unpublished optional instruments", body: runtimeMetrics},
		{name: "exporter names and labels", body: runtimeMetrics + `terrabase_storage_reads_total{path="point"} 100
terrabase_storage_reads_total{note="a } and \" quote", path="feed"} 7
terrabase_replication_merged_groups_total{region="gru"} 3
terrabase_replication_merged_groups_total{region="sjc"} 2
dotnet_process_cpu_time_seconds_total_created 999
dotnet_gc_heap_total_allocated_bytes_total_extra 999
`, feed: 7, merged: 5},
		{name: "storage instrument without feed activity", body: runtimeMetrics + "terrabase_storage_reads_total{path=\"point\"} 4\n"},
		{name: "published merged instrument missing sample", body: runtimeMetrics + "# TYPE terrabase_replication_merged_groups_total counter\n", wantErr: "missing metric sample terrabase_replication_merged_groups_total"},
		{name: "missing CPU is not zero", body: "dotnet_gc_heap_total_allocated_bytes_total 0\n", wantErr: "missing metric sample dotnet_process_cpu_time_seconds_total"},
		{name: "missing allocations is not zero", body: "dotnet_process_cpu_time_seconds_total 0\n", wantErr: "missing metric sample dotnet_gc_heap_total_allocated_bytes_total"},
		{name: "wrong unit suffix", body: "dotnet_process_cpu_time_milliseconds_total 1\ndotnet_gc_heap_total_allocated_bytes_total 0\n", wantErr: "missing metric sample dotnet_process_cpu_time_seconds_total"},
		{name: "overflow missing path", body: runtimeMetrics + "terrabase_storage_reads_total{otel_metric_overflow=\"true\"} 9\n", wantErr: "missing path label"},
		{name: "nonfinite sample", body: runtimeMetrics + "terrabase_replication_merged_groups_total NaN\n", wantErr: "invalid cumulative value"},
		{name: "negative sample", body: runtimeMetrics + "terrabase_replication_merged_groups_total -1\n", wantErr: "invalid cumulative value"},
		{name: "malformed sample", body: runtimeMetrics + "terrabase_replication_merged_groups_total\n", wantErr: "missing value"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				fmt.Fprint(w, tc.body)
			}))
			t.Cleanup(server.Close)
			s, err := Scrape(t.Context(), server.URL)
			if s.Raw != tc.body {
				t.Fatal("raw scrape was changed")
			}
			if tc.wantErr != "" {
				if err == nil || !strings.Contains(err.Error(), tc.wantErr) {
					t.Fatalf("error %v, want %q", err, tc.wantErr)
				}
				return
			}
			if err != nil || s.CPUms != 1750 || s.Allocated != 4096 || s.FeedReads != tc.feed || s.MergedGroups != tc.merged {
				t.Fatalf("snapshot %+v, error %v", s, err)
			}
		})
	}
}

func TestScrapeHTTPFailureCancellationAndReset(t *testing.T) {
	t.Parallel()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/failure" {
			w.WriteHeader(http.StatusServiceUnavailable)
			fmt.Fprint(w, "unavailable")
			return
		}
		fmt.Fprint(w, "dotnet_process_cpu_time_seconds_total 0\ndotnet_gc_heap_total_allocated_bytes_total 0\n")
	}))
	t.Cleanup(server.Close)
	if s, err := Scrape(t.Context(), server.URL+"/failure"); err == nil || s.Raw != "unavailable" {
		t.Fatalf("HTTP failure: %+v, %v", s, err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := Scrape(ctx, server.URL); err == nil {
		t.Fatal("canceled scrape succeeded")
	}
	// Zero is a valid post-restart counter, distinct from missing telemetry; the caller checks PID/deltas.
	if s, err := Scrape(t.Context(), server.URL); err != nil || s.CPUms != 0 || s.Allocated != 0 {
		t.Fatalf("reset snapshot: %+v, %v", s, err)
	}
}
