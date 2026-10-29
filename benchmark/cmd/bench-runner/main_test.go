package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"terrascale.tech/ts-benchmark/internal/benchmark"
	"testing"
)

func TestRepeatedAggregationKeepsDistinctReports(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	w := benchmark.Workload{Name: "point-reads", DurationS: 1, Concurrency: 1, Dataset: benchmark.Dataset{Documents: 1, DocSizeBytes: 32}, Mix: []benchmark.Mix{{Op: "doc.get", Weight: 100}}, Targets: benchmark.Targets{P99MS: 100}}
	body, err := json.Marshal(w)
	if err != nil {
		t.Fatal(err)
	}
	workload, input := filepath.Join(dir, "workload.json"), filepath.Join(dir, "client.jsonl")
	if err := os.WriteFile(workload, body, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(input, clientFixture(t, w, ""), 0600); err != nil {
		t.Fatal(err)
	}
	args := []string{"bench-runner", "--input", input, "--workload", workload, "--region", "gru", "--out-dir", dir, "--store=false"}
	previous := map[string][]byte{}
	for invocation := 1; invocation <= 2; invocation++ {
		if err := run(args[1:]); err != nil {
			t.Fatalf("aggregation %d failed: %v", invocation, err)
		}
		reports, err := filepath.Glob(filepath.Join(dir, "bench", "gru", "*", "report.json"))
		if err != nil || len(reports) != invocation {
			t.Fatal("missing distinct run evidence", reports, err)
		}
		published, err := filepath.Glob(filepath.Join(dir, "reports", "gru", "*.md"))
		if err != nil || len(published) != invocation {
			t.Fatal("missing distinct published reports", published, err)
		}
		for _, path := range reports {
			body, err := os.ReadFile(path)
			if err != nil {
				t.Fatal(err)
			}
			var report benchmark.Report
			if err := json.Unmarshal(body, &report); err != nil {
				t.Fatal(err)
			}
			name := report.Start.UTC().Format("2006-01-02T150405.000000000Z") + "-" + w.Name + "-" + report.ID + ".md"
			publishedPath := filepath.Join(dir, "reports", "gru", name)
			for _, evidence := range []string{path, filepath.Join(filepath.Dir(path), "summary.md"), publishedPath} {
				body, err := os.ReadFile(evidence)
				if err != nil {
					t.Fatal(err)
				}
				if original, ok := previous[evidence]; ok && !bytes.Equal(body, original) {
					t.Fatal("earlier evidence overwritten", evidence)
				}
				previous[evidence] = body
			}
		}
	}
}

func TestFailedClientRetainsEvidenceWithoutSecrets(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	client := filepath.Join(dir, "client")
	secret := "test-secret-must-never-enter-evidence"
	if err := os.WriteFile(client, []byte("#!/bin/sh\nprintf '%s' "+secret+" >&2\nexit 1\n"), 0700); err != nil {
		t.Fatal(err)
	}
	w := benchmark.Workload{Name: "point-reads", DurationS: 1, Concurrency: 1, Dataset: benchmark.Dataset{Documents: 1, DocSizeBytes: 32}, Mix: []benchmark.Mix{{Op: "doc.get", Weight: 100}}, Targets: benchmark.Targets{P99MS: 100}}
	body, _ := json.Marshal(w)
	path := filepath.Join(dir, "workload.json")
	if err := os.WriteFile(path, body, 0600); err != nil {
		t.Fatal(err)
	}
	args := []string{"bench-runner", "--client", client, "--workload", path, "--region", "gru", "--out-dir", dir, "--store=false"}
	err := run(args[1:])
	if err == nil || strings.Contains(err.Error(), secret) {
		t.Fatal("failure or secret handling failed")
	}
	receipts, _ := filepath.Glob(filepath.Join(dir, "bench", "gru", "*", "failure.json"))
	if len(receipts) != 1 {
		t.Fatal("missing failure receipt")
	}
	b, e := os.ReadFile(receipts[0])
	if e != nil || strings.Contains(string(b), secret) {
		t.Fatal("unsafe failure receipt")
	}
	tables, _ := filepath.Glob(filepath.Join(dir, "bench", "gru", "*", "summary.md"))
	if len(tables) != 1 {
		t.Fatal("missing Markdown failure table")
	}
	b, e = os.ReadFile(tables[0])
	if e != nil || !strings.Contains(string(b), "unavailable") || strings.Contains(string(b), secret) {
		t.Fatal("unsafe or misleading table")
	}
}
