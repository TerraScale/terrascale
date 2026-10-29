package apibench

import (
	"bytes"
	"context"
	"crypto/x509"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"syscall"
	"testing"
	"time"
)

func TestWriteReportRowsAndNoOverwrite(t *testing.T) {
	t.Parallel()
	r := Run{
		ID: "01J0TESTRUN", Host: "bench-host", ClusterState: "stable", Endpoint: "https://example.invalid",
		Start: time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC), End: time.Date(2026, 10, 11, 12, 5, 0, 0, time.UTC),
		Clients: []ClientRun{{
			Client:  "go",
			Ready:   &Ready{SDK: "ts-go", SDKVersion: "v0.1.0", Runtime: "go1.25.3", Database: "bench-01J0"},
			Results: []Result{{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 4096, Mode: "closed", Concurrency: 8, RPS: 1234.5, DurationS: 10, Status: map[string]int64{"ok": 1230, "429": 4}, Latency: Latency{P50: 6100, P99: 9100}}},
			Done:    &Done{Cleanup: true},
		}},
	}
	dir := t.TempDir()
	md, js, err := WriteReport(dir, r)
	if err != nil {
		t.Fatal(err)
	}
	if filepath.Base(md) != "2026-10-11-bench-host-01J0TESTRUN.md" || filepath.Base(js) != "2026-10-11-bench-host-01J0TESTRUN.json" {
		t.Fatalf("paths %s %s", md, js)
	}
	b, err := os.ReadFile(md)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"### doc.get", "| go | sdk | 4096 | closed | 8 | - | 1234.5 | 6.10 |", "429:4 ok:1230"} {
		if !strings.Contains(string(b), want) {
			t.Fatalf("report missing %q:\n%s", want, b)
		}
	}
	if _, _, err := WriteReport(dir, r); err == nil {
		t.Fatal("overwrote an existing report")
	}
}

func TestNewStat(t *testing.T) {
	t.Parallel()
	if s := NewStat([]float64{4, 1, 3, 2}); s.Min != 1 || s.Median != 2.5 {
		t.Fatalf("got %+v", s)
	}
}

func TestServeAnswersEachStep(t *testing.T) {
	t.Parallel()
	in := bytes.NewBufferString(`{"type":"step","scenario":"doc.get","transport":"sdk","payload_bytes":256,"mode":"closed","concurrency":1,"warmup_s":0,"step_s":1,"abort_error_rate":0.01,"abort_p99_us":0}` + "\n")
	var out bytes.Buffer
	err := Serve(in, &out, Ready{Client: "go"}, func(s Step) Result { return Result{Scenario: s.Scenario, OK: 1} })
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 2 || !strings.Contains(lines[0], `"type":"ready"`) || !strings.Contains(lines[1], `"scenario":"doc.get"`) {
		t.Fatalf("output %q", out.String())
	}
	if err := Serve(bytes.NewBufferString(`{"bogus":1}`+"\n"), &out, Ready{}, nil); err == nil {
		t.Fatal("accepted an unknown step field")
	}
	out.Reset()
	open0 := `{"type":"step","scenario":"doc.get","mode":"open","concurrency":1,"rate":0,"step_s":1}`
	if err := Serve(bytes.NewBufferString(open0+"\n"), &out, Ready{}, nil); err == nil || strings.Count(out.String(), "\n") != 1 {
		t.Fatalf("open rate 0: err %v, output %q; want an error after the ready line only", err, out.String())
	}
}

func TestProbeEndpointReusesHandshake(t *testing.T) {
	t.Parallel()
	var conns, healthz atomic.Int64
	srv := httptest.NewUnstartedServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { healthz.Add(1) }))
	srv.Config.ConnState = func(_ net.Conn, s http.ConnState) {
		if s == http.StateNew {
			conns.Add(1)
		}
	}
	srv.StartTLS()
	defer srv.Close()
	pool := x509.NewCertPool()
	pool.AddCert(srv.Certificate())
	if _, err := probeEndpoint(context.Background(), srv.URL, 3, pool); err != nil {
		t.Fatal(err)
	}
	if conns.Load() != 3 || healthz.Load() != 3 {
		t.Fatalf("3 tries: %d TCP connections, %d healthz requests; want 3 and 3", conns.Load(), healthz.Load())
	}
}

func TestWriteReportCleansUpAfterWriteError(t *testing.T) {
	t.Parallel()
	// RLIMIT_FSIZE is process-wide: keep the real failing write in a child so
	// parallel report tests retain their normal limits.
	if os.Getenv("TS_BENCH_REPORT_FSIZE_CHILD") != "1" {
		child := exec.CommandContext(t.Context(), os.Args[0], "-test.run=^TestWriteReportCleansUpAfterWriteError$")
		child.Env = append(os.Environ(), "TS_BENCH_REPORT_FSIZE_CHILD=1")
		if output, err := child.CombinedOutput(); err != nil {
			t.Fatalf("report write-failure child: %v\n%s", err, output)
		}
		return
	}
	r := Run{ID: "01J0FAILRUN", Host: "bench-host", Start: time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC)}
	var old syscall.Rlimit
	if err := syscall.Getrlimit(syscall.RLIMIT_FSIZE, &old); err != nil {
		t.Fatal(err)
	}
	// A 64-byte file size limit makes the write fail with EFBIG.
	if err := syscall.Setrlimit(syscall.RLIMIT_FSIZE, &syscall.Rlimit{Cur: 64, Max: old.Max}); err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	_, _, werr := WriteReport(dir, r)
	if err := syscall.Setrlimit(syscall.RLIMIT_FSIZE, &old); err != nil {
		t.Fatal(err)
	}
	if werr == nil {
		t.Fatal("write under a 64-byte limit succeeded")
	}
	if left, _ := os.ReadDir(dir); len(left) != 0 {
		t.Fatalf("failed write left %d files", len(left))
	}
	if _, _, err := WriteReport(dir, r); err != nil {
		t.Fatal(err)
	}
}
