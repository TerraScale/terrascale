package main

import (
	"encoding/json"
	"net/url"
	"strings"
	"testing"
	"time"
)

func ptr[T any](value T) *T { return &value }

func validFixture() Result {
	stages := make([]StageResult, 0, len(requiredStages))
	for _, readers := range requiredStages {
		total, hit, worker, ratio, cost := uint64(1000), uint64(900), uint64(0), .9, .12
		stages = append(stages, StageResult{
			Readers: readers, WriteRate: 16, StartedAt: time.Unix(1, 0), EndedAt: time.Unix(2, 0),
			Duration: 1, Requests: 10, Successes: 10, Latency: Latency{Samples: 10, P50: 1, P95: 2, P99: 3, Max: 4},
			Cache:  CacheMetrics{EdgeMisses: ptr(uint64(2)), UpperTierMisses: ptr(uint64(1)), OriginFetches: ptr(uint64(1)), TotalBytes: &total, CacheHitBytes: &hit, ByteHitRatio: &ratio},
			Worker: &worker, CostPerMillion: &cost, OrderedBytes: OrderedBytes{OK: true, Responses: 10}, Isolation: Isolation{OK: true, Attempts: 1, Denied: 1},
		})
	}
	return Result{
		SchemaVersion: resultSchemaVersion, RunID: "run-1", Endpoint: "https://sync.example/log", WriteRate: 16,
		StartedAt: time.Unix(1, 0), EndedAt: time.Unix(2, 0),
		Evidence:   Evidence{Mode: "real-zone", CredentialsUsed: true, TelemetrySource: "https://metrics.example", InjectionSource: "https://control.example", TelemetryComplete: true, InjectionsRun: true},
		Stages:     stages,
		Injections: []InjectionTest{{Name: "cache-bypass", Requested: true, Observed: true, NoLeak: true}, {Name: "eviction", Requested: true, Observed: true, NoLeak: true}, {Name: "rule-change", Requested: true, Observed: true, NoLeak: true}, {Name: "capability-expiry", Requested: true, Observed: true, NoLeak: true}},
	}
}

func TestValidatorAcceptsCompleteRealEvidence(t *testing.T) {
	if err := validateResult(validFixture(), false); err != nil {
		t.Fatal(err)
	}
}

func TestValidatorRejectsMissingTelemetryAndWorkerMetric(t *testing.T) {
	fixture := validFixture()
	fixture.Evidence.TelemetryComplete = false
	fixture.Stages[0].Worker = nil
	if err := validateResult(fixture, false); err == nil || !strings.Contains(err.Error(), "telemetry") || !strings.Contains(err.Error(), "Worker") {
		t.Fatalf("expected fail-closed telemetry and Worker errors, got %v", err)
	}
}

func TestValidatorRejectsNonZeroWorkerAndLeakedBytes(t *testing.T) {
	fixture := validFixture()
	worker := uint64(1)
	fixture.Stages[1].Worker = &worker
	fixture.Stages[1].Isolation.LeakedBytes = 1
	if err := validateResult(fixture, false); err == nil || !strings.Contains(err.Error(), "Worker") || !strings.Contains(err.Error(), "isolation") {
		t.Fatalf("expected zero-worker/isolation errors, got %v", err)
	}
}

func TestValidatorRejectsMissingInjection(t *testing.T) {
	fixture := validFixture()
	fixture.Injections = fixture.Injections[:3]
	if err := validateResult(fixture, false); err == nil || !strings.Contains(err.Error(), "capability-expiry") {
		t.Fatalf("expected missing expiry injection error, got %v", err)
	}
}

func TestFrameRevisionReadsStubShape(t *testing.T) {
	// map(3){"revision": 7, "cut": 7, "deltas": []}
	frame := []byte{0x83, 0xa8, 'r', 'e', 'v', 'i', 's', 'i', 'o', 'n', 7, 0xa3, 'c', 'u', 't', 7, 0xa6, 'd', 'e', 'l', 't', 'a', 's', 0x90}
	revision, err := frameRevision(frame)
	if err != nil || revision != 7 {
		t.Fatalf("revision=%d err=%v", revision, err)
	}
}

func TestFrameRevisionReadsStringRevision(t *testing.T) {
	frame := []byte{0x83, 0xa8, 'r', 'e', 'v', 'i', 's', 'i', 'o', 'n', 0xa2, '1', '7', 0xa3, 'c', 'u', 't', 1, 0xa6, 'd', 'e', 'l', 't', 'a', 's', 0x90}
	revision, err := frameRevision(frame)
	if err != nil || revision != 17 {
		t.Fatalf("revision=%d err=%v", revision, err)
	}
}

func TestResultJSONKeepsUnavailableMetricsNull(t *testing.T) {
	result := Result{SchemaVersion: resultSchemaVersion, Stages: []StageResult{{Cache: CacheMetrics{}}}}
	encoded, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(encoded), `"edge_misses":null`) {
		t.Fatalf("nil metrics must remain explicit null: %s", encoded)
	}
}

func TestRunConfigurationRejectsUnsafeEndpoints(t *testing.T) {
	if _, err := checkedEndpoint("http://example.invalid/log", true); err == nil {
		t.Fatal("non-loopback HTTP must be rejected")
	}
	if _, err := checkedEndpoint("https://sync.example/log", false); err != nil {
		t.Fatal(err)
	}
	if _, err := checkedEndpoint("https://user:pass@sync.example/log", false); err == nil {
		t.Fatal("endpoint userinfo must be rejected")
	}
	for _, endpoint := range []string{"http://metrics.example/read", "http://control.example/inject"} {
		if _, err := checkedOptionalEndpoint(endpoint, true, "auxiliary"); err == nil {
			t.Fatalf("credential-bearing non-loopback endpoint %q must be rejected", endpoint)
		}
	}
	if got, err := checkedOptionalEndpoint("http://127.0.0.1:8080/read", true, "auxiliary"); err != nil || got == "" {
		t.Fatalf("loopback auxiliary endpoint rejected: got=%q err=%v", got, err)
	}
	if _, err := checkedOptionalEndpoint("http://127.0.0.1:8080/read", false, "auxiliary"); err == nil {
		t.Fatal("loopback HTTP without --allow-http must be rejected")
	}
}

func TestOffsetIsRewrittenWithoutDroppingOtherQueryValues(t *testing.T) {
	endpoint, err := url.Parse("https://sync.example/log?offset=2&ignored=x")
	if err != nil {
		t.Fatal(err)
	}
	got, err := url.Parse(withOffset(endpoint, 17))
	if err != nil {
		t.Fatal(err)
	}
	if got.Query().Get("offset") != "17" || got.Query().Get("ignored") != "x" {
		t.Fatalf("query = %v", got.Query())
	}
}

func TestLoadResultRejectsTrailingJSON(t *testing.T) {
	if _, err := LoadResult(strings.NewReader(`{"schema_version":1} {"schema_version":1}`)); err == nil {
		t.Fatal("trailing JSON must be rejected")
	}
}
