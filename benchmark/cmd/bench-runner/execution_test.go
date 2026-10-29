package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	hdr "github.com/HdrHistogram/hdrhistogram-go"
	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/benchmark"
)

func clientFixture(t *testing.T, w benchmark.Workload, stopped string) []byte {
	t.Helper()
	h := hdr.New(1, benchmark.HistogramMaxUS, 3)
	if err := h.RecordValue(1000); err != nil {
		t.Fatal(err)
	}
	stats := benchmark.Stats{Count: 1, P50US: h.ValueAtQuantile(50), P95US: h.ValueAtQuantile(95), P99US: h.ValueAtQuantile(99), MaxUS: h.Max(), Histogram: h.Export()}
	concurrencies, seconds := []int{w.Concurrency}, w.DurationS
	if w.Ramp != nil {
		concurrencies, seconds = w.Ramp.Concurrency, w.Ramp.StepS
	}
	var b bytes.Buffer
	enc := json.NewEncoder(&b)
	var steps []benchmark.Step
	start := time.Date(2026, 10, 8, 0, 0, 0, 0, time.UTC)
	for i, concurrency := range concurrencies {
		step := benchmark.Step{Concurrency: concurrency, Start: start.Add(time.Duration(i*seconds) * time.Second), ElapsedS: float64(seconds), OpsPerS: 1 / float64(seconds), PerOp: map[string]benchmark.Stats{"doc.get": stats}, Completed: stopped == "" || i < len(concurrencies)-1}
		step.End = step.Start.Add(time.Duration(seconds) * time.Second)
		steps = append(steps, step)
		for second := 0; second < seconds; second++ {
			row := benchmark.Window{TS: step.Start.Add(time.Duration(second) * time.Second), Client: "go", SDK: "ts-go", SDKVersion: "test", Region: "gru", Op: "doc.get", Step: i, Concurrency: concurrency, ElapsedS: 1}
			if second == 0 {
				row.Stats = stats
				row.Histogram = nil
			}
			if err := enc.Encode(row); err != nil {
				t.Fatal(err)
			}
		}
	}
	s, err := benchmark.Summarize(ulid.Make().String(), "gru", w, steps, true, stopped)
	if err != nil {
		t.Fatal(err)
	}
	if err := enc.Encode(s); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}

func TestClientExitsAppearInRetainedReports(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name, verdict string
		ramp, failed  bool
		stops         []string
		exits         []int
		statuses      []string
	}{
		{"successful process", "PASS", false, false, []string{""}, []int{0}, []string{"success"}},
		{"failed process after passing summary", "FAIL", false, true, []string{""}, []int{1}, []string{"failed"}},
		{"expected ramp safety exit", "PASS", true, false, []string{"ramp safety threshold exceeded"}, []int{1}, []string{"safety-stop"}},
		{"safety exit does not excuse another client", "FAIL", true, true, []string{"ramp safety threshold exceeded", ""}, []int{1, 1}, []string{"safety-stop", "failed"}},
	} {
		t.Run(test.name, func(t *testing.T) {
			dir := t.TempDir()
			w := benchmark.Workload{Name: "point-reads", DurationS: 1, Concurrency: 1, Dataset: benchmark.Dataset{Documents: 1, DocSizeBytes: 32}, Mix: []benchmark.Mix{{Op: "doc.get", Weight: 100}}, Targets: benchmark.Targets{P99MS: 100}}
			if test.ramp {
				w.Name, w.DurationS = "ramp", 10
				w.Ramp = &benchmark.Ramp{StepS: 5, Concurrency: []int{1, 2}}
			}
			body, err := json.Marshal(w)
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(dir, "workload.json")
			if err := os.WriteFile(path, body, 0600); err != nil {
				t.Fatal(err)
			}
			args := []string{"bench-runner", "--workload", path, "--region", "gru", "--out-dir", dir, "--store=false"}
			for i, stop := range test.stops {
				client := filepath.Join(dir, fmt.Sprintf("client-%d", i))
				script := fmt.Sprintf("#!/bin/sh\nwhile [ \"$1\" != \"--out\" ]; do shift; done\ncp \"$0.jsonl\" \"$2\"\nexit %d\n", test.exits[i])
				if err := os.WriteFile(client, []byte(script), 0700); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(client+".jsonl", clientFixture(t, w, stop), 0600); err != nil {
					t.Fatal(err)
				}
				args = append(args, "--client", client)
			}
			if err := run(args[1:]); (err != nil) != test.failed {
				t.Fatal("unexpected runner exit", err)
			}
			files, _ := filepath.Glob(filepath.Join(dir, "bench", "gru", "*", "report.json"))
			if len(files) != 1 {
				t.Fatal("missing report")
			}
			body, err = os.ReadFile(files[0])
			if err != nil {
				t.Fatal(err)
			}
			var report benchmark.Report
			if err := json.Unmarshal(body, &report); err != nil || report.Verdict != test.verdict || len(report.ClientExecution) != len(test.statuses) {
				t.Fatal("execution failure absent from JSON report", err)
			}
			for i, status := range test.statuses {
				if report.ClientExecution[report.Runs[i].RunID] != status {
					t.Fatal("incorrect process status")
				}
			}
			tables, _ := filepath.Glob(filepath.Join(dir, "reports", "gru", "*.md"))
			tables = append(tables, filepath.Join(filepath.Dir(files[0]), "summary.md"))
			if len(tables) != 2 {
				t.Fatal("missing Markdown report")
			}
			for _, table := range tables {
				body, err := os.ReadFile(table)
				if err != nil || !strings.Contains(string(body), "Capacity verdict: "+test.verdict) {
					t.Fatal("execution failure absent from Markdown verdict", err)
				}
				for _, status := range test.statuses {
					if !strings.Contains(string(body), "| "+status+" |") {
						t.Fatal("execution status absent from Markdown")
					}
				}
			}
		})
	}
}

func TestSequentialBudgetCheckedBeforeLaunch(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name                      string
		clients, duration, warmup int
		ramp, rejected            bool
	}{
		{"one longest client", 1, 600, 60, false, false},
		{"two long clients", 2, 600, 0, false, true},
		{"exact budget with warmup", 2, 400, 35, false, false},
		{"warmup exceeds budget", 2, 400, 36, false, true},
		{"three clients at budget", 3, 290, 0, false, false},
		{"three clients exceed budget", 3, 291, 0, false, true},
		{"two complete ramps exceed budget", 2, 600, 0, true, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			dir := t.TempDir()
			w := benchmark.Workload{Name: "point-reads", DurationS: test.duration, WarmupS: test.warmup, Concurrency: 1, Dataset: benchmark.Dataset{Documents: 1, DocSizeBytes: 32}, Mix: []benchmark.Mix{{Op: "doc.get", Weight: 100}}, Targets: benchmark.Targets{P99MS: 100}}
			if test.ramp {
				w.Name = "ramp"
				w.Ramp = &benchmark.Ramp{StepS: test.duration / 2, Concurrency: []int{1, 2}}
			}
			body, err := json.Marshal(w)
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(dir, "workload.json")
			if err := os.WriteFile(path, body, 0600); err != nil {
				t.Fatal(err)
			}
			args := []string{"bench-runner", "--workload", path, "--region", "gru", "--out-dir", dir, "--store=false"}
			var clients []string
			for i := 0; i < test.clients; i++ {
				client := filepath.Join(dir, fmt.Sprintf("client-%d", i))
				script := "#!/bin/sh\ntouch \"$0.launched\"\nwhile [ \"$1\" != \"--out\" ]; do shift; done\ncp \"$0.jsonl\" \"$2\"\n"
				if err := os.WriteFile(client, []byte(script), 0700); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(client+".jsonl", clientFixture(t, w, ""), 0600); err != nil {
					t.Fatal(err)
				}
				clients = append(clients, client)
				args = append(args, "--client", client)
			}
			err = run(args[1:])
			if test.rejected {
				if err == nil || !strings.Contains(err.Error(), "shared execution budget") {
					t.Fatal("missing budget rejection", err)
				}
				if _, err := os.Stat(filepath.Join(dir, "bench")); !os.IsNotExist(err) {
					t.Fatal("created evidence directory before rejecting workload")
				}
			} else if err != nil {
				t.Fatal("valid schedule rejected", err)
			}
			for _, client := range clients {
				_, err := os.Stat(client + ".launched")
				if test.rejected && !os.IsNotExist(err) || !test.rejected && err != nil {
					t.Fatal("unexpected client launch state", client, err)
				}
			}
		})
	}
}
