package main

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func testConfig() Config {
	return Config{
		Endpoint:     "127.0.0.1:443",
		RootCertPath: "/tmp/root.pem", ClientCertPath: "/tmp/client.pem", ClientKeyPath: "/tmp/client.key",
		Principal: "bench", Tenant: "benchmark-tenant", ServerIdentity: "server-1",
		SourceIdentity: "source-1", TransportIdentity: "Go terrabase-go mTLS",
		Workload: "read", OutputPath: "/tmp/result.json", Timeout: time.Minute,
		Lane: LaneConfig{SeedDocuments: 8, Operations: 8, Samples: 2, Concurrency: 2, WarmupOperations: 4, NoisePercent: 5},
	}
}

func TestConfigValidationBoundsAndDivisibility(t *testing.T) {
	config := testConfig()
	if err := config.Validate(); err != nil {
		t.Fatalf("valid config rejected: %v", err)
	}
	config.Lane.Operations++
	if err := config.Validate(); err == nil || !strings.Contains(err.Error(), "divisible") {
		t.Fatalf("non-divisible operations accepted: %v", err)
	}
	config = testConfig()
	config.Workload = "delete"
	if err := config.Validate(); err == nil {
		t.Fatal("unknown workload accepted")
	}
	config = testConfig()
	config.Lane.Concurrency = maxSlots + 1
	if err := config.Validate(); err == nil {
		t.Fatal("unbounded concurrency accepted")
	}
}

func TestWorkloadSelection(t *testing.T) {
	workload, err := ParseWorkload("mixed")
	if err != nil {
		t.Fatal(err)
	}
	want := []Workload{WorkloadRead, WorkloadRead, WorkloadRead, WorkloadRead, WorkloadRead, WorkloadWrite, WorkloadWrite, WorkloadWrite, WorkloadQuery, WorkloadQuery}
	for index, expected := range want {
		if got := workload.SelectMixed(index); got != expected {
			t.Fatalf("index %d: got %v, want %v", index, got, expected)
		}
	}
}

func TestSamplePercentilesAndResultJSON(t *testing.T) {
	sample := SampleOf(4, 2, []float64{40, 10, 30, 20})
	if sample.P50US != 30 || sample.P95US != 40 || sample.P99US != 40 || sample.ThroughputOpsPerS != 2 {
		t.Fatalf("unexpected sample: %+v", sample)
	}
	result := RunResults{
		Status: "complete", Target: targetName, Workload: "read", Config: testConfig().Lane,
		Environment: Environment{ObservedIdentity: "server", Endpoint: "127.0.0.1:443", ClientTransport: "mTLS", UnixSeconds: 1, Kernel: "Linux test", LogicalCPUs: 1},
		Samples:     []Sample{sample}, Limits: []string{"test limit"},
	}
	data, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{`"status":"complete"`, `"target":"terrabase"`, `"workload":"read"`, `"p50_us":30`} {
		if !strings.Contains(string(data), field) {
			t.Fatalf("result JSON misses %s: %s", field, data)
		}
	}
}

func TestWriteResultIsExclusive(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nested", "result.json")
	if err := WriteResultExclusive(path, map[string]string{"status": "complete"}); err != nil {
		t.Fatal(err)
	}
	if err := WriteResultExclusive(path, map[string]string{"status": "failed"}); !os.IsExist(err) {
		t.Fatalf("second write did not fail immutably: %v", err)
	}
	data, err := os.ReadFile(path)
	if err != nil || !strings.Contains(string(data), `"status": "complete"`) {
		t.Fatalf("first result was not retained: %s (%v)", data, err)
	}
}

type fakeSlot struct {
	mu         sync.Mutex
	failedAt   int
	operations int
}

func (s *fakeSlot) read(context.Context, int) error     { return s.operation() }
func (s *fakeSlot) write(context.Context, uint64) error { return s.operation() }
func (s *fakeSlot) query(context.Context, int) error    { return s.operation() }
func (s *fakeSlot) close() error                        { return nil }
func (s *fakeSlot) operation() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	index := s.operations
	s.operations++
	if s.failedAt >= 0 && index == s.failedAt {
		return errors.New("synthetic operation failure")
	}
	return nil
}

func TestRunSlotsDropsAllSamplesOnFailure(t *testing.T) {
	config := testConfig()
	config.Lane.WarmupOperations = 0
	config.Lane.Operations = 4
	config.Lane.Samples = 3
	config.Lane.Concurrency = 2
	slots := []slot{&fakeSlot{failedAt: 0}, &fakeSlot{failedAt: -1}}
	result, err := runSlots(context.Background(), config, slots)
	if err == nil || !strings.Contains(err.Error(), "synthetic operation failure") {
		t.Fatalf("failure was not returned: result=%+v err=%v", result, err)
	}
	if len(result.Samples) != 0 {
		t.Fatalf("failed run returned partial samples: %+v", result.Samples)
	}
}

func TestRunSlotsStopsAfterWarmupFailure(t *testing.T) {
	config := testConfig()
	config.Lane.WarmupOperations = 2
	config.Lane.Operations = 4
	config.Lane.Samples = 2
	config.Lane.Concurrency = 2
	slots := []slot{&fakeSlot{failedAt: 0}, &fakeSlot{failedAt: -1}}
	result, err := runSlots(context.Background(), config, slots)
	if err == nil || !strings.Contains(err.Error(), "synthetic operation failure") {
		t.Fatalf("warm-up failure was not returned: result=%+v err=%v", result, err)
	}
	if len(result.Samples) != 0 {
		t.Fatalf("failed warm-up returned samples: %+v", result.Samples)
	}
}

func TestRunSlotsProducesBarrieredSamples(t *testing.T) {
	config := testConfig()
	slots := []slot{&fakeSlot{failedAt: -1}, &fakeSlot{failedAt: -1}}
	result, err := runSlots(context.Background(), config, slots)
	if err != nil {
		t.Fatal(err)
	}
	if result.Status != "complete" || len(result.Samples) != config.Lane.Samples {
		t.Fatalf("unexpected result: %+v", result)
	}
	for _, sample := range result.Samples {
		if sample.Operations != uint64(config.Lane.Operations) || sample.WallSeconds <= 0 {
			t.Fatalf("invalid sample: %+v", sample)
		}
	}
}
