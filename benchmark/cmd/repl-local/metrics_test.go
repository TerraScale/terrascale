package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
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
			s, err := scrape(t.Context(), server.URL)
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
	if s, err := scrape(t.Context(), server.URL+"/failure"); err == nil || s.Raw != "unavailable" {
		t.Fatalf("HTTP failure: %+v, %v", s, err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := scrape(ctx, server.URL); err == nil {
		t.Fatal("canceled scrape succeeded")
	}
	// Zero is a valid post-restart counter, distinct from missing telemetry; the caller checks PID/deltas.
	if s, err := scrape(t.Context(), server.URL); err != nil || s.CPUms != 0 || s.Allocated != 0 {
		t.Fatalf("reset snapshot: %+v, %v", s, err)
	}
}

func TestWireProxyAccountingHalfCloseAndRetarget(t *testing.T) {
	t.Parallel()
	p, err := startWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	request := bytes.Repeat([]byte("request"), 20_000)
	var want int64
	for _, suffix := range []string{"first", "restarted"} {
		response := append(bytes.Repeat([]byte("reply"), 20_000), suffix...)
		target := wireTestPeer(t, func(conn net.Conn) error {
			got, err := io.ReadAll(conn)
			if err != nil || !bytes.Equal(got, request) {
				return fmt.Errorf("request mismatch or read error: %v", err)
			}
			_, err = conn.Write(response)
			return err
		})
		if err := p.Target("http://" + target.Addr().String()); err != nil {
			t.Fatal(err)
		}
		conn := wireTestDial(t, p)
		if _, err := conn.Write(request); err != nil {
			t.Fatal(err)
		}
		if err := conn.CloseWrite(); err != nil {
			t.Fatal(err)
		}
		got, err := io.ReadAll(conn)
		if err != nil || !bytes.Equal(got, response) {
			t.Fatalf("response mismatch or read error: %v", err)
		}
		want += int64(len(request) + len(response))
	}
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
	if p.Bytes() != want || p.upload.Load() != int64(2*len(request)) || p.download.Load() != want-int64(2*len(request)) {
		t.Fatalf("bytes %d, upload %d, download %d; want %d", p.Bytes(), p.upload.Load(), p.download.Load(), want)
	}
}

func TestWireProxyForwardsPlaintextHTTP2(t *testing.T) {
	t.Parallel()
	protocols := new(http.Protocols)
	protocols.SetUnencryptedHTTP2(true)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.ProtoMajor != 2 {
			t.Errorf("protocol %s, want HTTP/2", r.Proto)
		}
		fmt.Fprint(w, "peer response")
	}))
	server.Config.Protocols = protocols
	server.Start()
	t.Cleanup(server.Close)
	p, err := startWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	if err := p.Target(server.URL); err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Transport: &http.Transport{Protocols: protocols}, Timeout: 3 * time.Second}
	t.Cleanup(client.CloseIdleConnections)
	resp, err := client.Get(p.URL())
	if err != nil {
		t.Fatal(err)
	}
	body, err := io.ReadAll(resp.Body)
	resp.Body.Close()
	if err != nil || string(body) != "peer response" || resp.ProtoMajor != 2 {
		t.Fatalf("body %q, error %v", body, err)
	}
	client.CloseIdleConnections()
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
	if p.Bytes() <= int64(len(body)) {
		t.Fatalf("HTTP/2 framing and control traffic not counted: %d bytes", p.Bytes())
	}
}

func TestWireProxyCancellationClosesActiveConnections(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	p, err := startWireProxy(ctx)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	accepted := make(chan struct{})
	target := wireTestPeer(t, func(conn net.Conn) error {
		close(accepted)
		_, err := io.Copy(io.Discard, conn)
		return err
	})
	if err := p.Target("http://" + target.Addr().String()); err != nil {
		t.Fatal(err)
	}
	conn := wireTestDial(t, p)
	select {
	case <-accepted:
	case <-time.After(3 * time.Second):
		t.Fatal("proxy did not connect to peer")
	}
	cancel()
	done := make(chan error, 1)
	go func() { done <- p.Close() }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("Close did not join proxy goroutines")
	}
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("active client read: %v, want EOF", err)
	}
	if conn, err := net.DialTimeout("tcp", p.listener.Addr().String(), time.Second); err == nil {
		conn.Close()
		t.Fatal("listener remained open")
	}
}

func TestWireProxyTargetValidationAndDialError(t *testing.T) {
	t.Parallel()
	p, err := startWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	for _, raw := range []string{"https://127.0.0.1:1", "http://", "http://127.0.0.1:0", "http://127.0.0.1:65536", "http://user@127.0.0.1", "http://127.0.0.1/path", "http://127.0.0.1?x=1", "http://192.0.2.1:80"} {
		if err := p.Target(raw); err == nil {
			t.Fatalf("accepted invalid target %q", raw)
		}
	}
	conn := wireTestDial(t, p)
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("unset target read: %v", err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	if err := p.Target("http://" + listener.Addr().String()); err != nil {
		t.Fatal(err)
	}
	listener.Close()
	conn = wireTestDial(t, p)
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("unavailable target read: %v", err)
	}
	if err := p.Close(); err == nil || !strings.Contains(err.Error(), "peer dial") {
		t.Fatalf("dial failure was not retained: %v", err)
	}
}

func wireTestDial(t *testing.T, p *wireProxy) *net.TCPConn {
	t.Helper()
	conn, err := net.DialTimeout("tcp", strings.TrimPrefix(p.URL(), "http://"), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	if err := conn.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
		t.Fatal(err)
	}
	return conn.(*net.TCPConn)
}

func wireTestPeer(t *testing.T, handle func(net.Conn) error) net.Listener {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	wg.Go(func() {
		conn, err := listener.Accept()
		if err != nil {
			t.Errorf("peer accept: %v", err)
			return
		}
		defer conn.Close()
		if err := conn.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
			t.Errorf("peer deadline: %v", err)
			return
		}
		if err := handle(conn); err != nil {
			t.Errorf("peer: %v", err)
		}
	})
	t.Cleanup(func() { listener.Close(); wg.Wait() })
	return listener
}
