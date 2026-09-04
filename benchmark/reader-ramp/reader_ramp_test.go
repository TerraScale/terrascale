package main

import (
	"context"
	"encoding/json"
	"math"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func ptr[T any](value T) *T { return &value }

func validFixture() Result {
	stages := make([]StageResult, 0, len(requiredStages))
	for i, readers := range requiredStages {
		total, hit, worker, ratio, cost := uint64(1000), uint64(900), uint64(0), .9, .12
		start := time.Unix(int64(1+i*2), 0)
		end := start.Add(time.Second)
		stages = append(stages, StageResult{
			Readers: readers, WriteRate: 16, StartedAt: start, EndedAt: end,
			Duration: 1, Requests: 10, Successes: 10, Latency: Latency{Samples: 10, P50: 1, P95: 2, P99: 3, Max: 4},
			Cache:  CacheMetrics{EdgeMisses: ptr(uint64(2)), UpperTierMisses: ptr(uint64(1)), OriginFetches: ptr(uint64(1)), TotalBytes: &total, CacheHitBytes: &hit, ByteHitRatio: &ratio},
			Worker: &worker, CostPerMillion: &cost, OrderedBytes: OrderedBytes{OK: true, Responses: 10}, Isolation: Isolation{OK: true, Attempts: 1, Denied: 1},
		})
	}
	return Result{
		SchemaVersion: resultSchemaVersion, RunID: "run-1", Endpoint: "https://sync.example/log", WriteRate: 16,
		StartedAt: time.Unix(1, 0), EndedAt: time.Unix(8, 0),
		Evidence: Evidence{Mode: "real-zone", CredentialsUsed: true, TelemetrySource: "https://metrics.example", InjectionSource: "https://control.example", TelemetryComplete: true, InjectionsRun: true},
		Stages:   stages,
		Injections: []InjectionTest{
			{Name: "cache-bypass", Requested: true, Observed: true, NoLeak: true, ProbeStatus: 200, StartedAt: time.Unix(6, 0), EndedAt: time.Unix(6, 100000000)},
			{Name: "eviction", Requested: true, Observed: true, NoLeak: true, ProbeStatus: 200, StartedAt: time.Unix(6, 200000000), EndedAt: time.Unix(6, 300000000)},
			{Name: "rule-change", Requested: true, Observed: true, NoLeak: true, ProbeStatus: 200, StartedAt: time.Unix(6, 400000000), EndedAt: time.Unix(6, 500000000)},
			{Name: "capability-expiry", Requested: true, Observed: true, NoLeak: true, ProbeStatus: 401, StartedAt: time.Unix(6, 600000000), EndedAt: time.Unix(6, 700000000)},
		},
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

func TestValidatorRejectsStageErrorsAndInconsistentEvidence(t *testing.T) {
	tests := []struct {
		name string
		edit func(*Result)
		want string
	}{
		{"stage errors", func(result *Result) { result.Stages[0].Errors = 1 }, "request errors"},
		{"duration mismatch", func(result *Result) { result.Stages[0].Duration = 2 }, "duration does not match"},
		{"stage overlap", func(result *Result) { result.Stages[1].StartedAt = result.Stages[0].StartedAt }, "overlaps"},
		{"missing metric", func(result *Result) { result.Stages[0].Cache.OriginFetches = nil }, "incomplete cache"},
		{"infinite cost", func(result *Result) { *result.Stages[0].CostPerMillion = math.Inf(1) }, "cost"},
		{"ratio mismatch", func(result *Result) { *result.Stages[0].Cache.ByteHitRatio = .8 }, "byte-hit ratio does not match"},
		{"counter mismatch", func(result *Result) { *result.Stages[0].Cache.UpperTierMisses = 3 }, "cache byte-hit ratio is invalid"},
		{"non-identical bytes", func(result *Result) { result.Stages[0].OrderedBytes.Violations = 1 }, "ordered byte equality"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			result := validFixture()
			test.edit(&result)
			if err := validateResult(result, false); err == nil || !strings.Contains(err.Error(), test.want) {
				t.Fatalf("expected %q, got %v", test.want, err)
			}
		})
	}
}

func TestValidatorDerivesModeFromEndpointContext(t *testing.T) {
	result := validFixture()
	result.Endpoint = "http://127.0.0.1:8080/log"
	result.Evidence.Mode = "real-zone"
	if err := validateResult(result, true); err == nil || !strings.Contains(err.Error(), "does not match endpoint context") {
		t.Fatalf("spoofed mode was accepted: %v", err)
	}

	result = validFixture()
	result.Endpoint = "https://127.0.0.1:8080/log"
	result.Evidence.Mode = "real-zone"
	if err := validateResult(result, true); err == nil || !strings.Contains(err.Error(), "does not match endpoint context") {
		t.Fatalf("HTTPS loopback was accepted as production evidence: %v", err)
	}

	result = validFixture()
	result.Evidence.Mode = "stub"
	if err := validateResult(result, false); err == nil || !strings.Contains(err.Error(), "does not match endpoint context") {
		t.Fatalf("spoofed real-zone context was accepted: %v", err)
	}
}

func TestIsolationRequiresExactCapabilityDenial(t *testing.T) {
	for _, test := range []struct {
		name   string
		status int
		wantOK bool
	}{
		{name: "401 unauthorized", status: http.StatusUnauthorized, wantOK: true},
		{name: "200 JSON", status: http.StatusOK, wantOK: false},
		{name: "404 not found", status: http.StatusNotFound, wantOK: false},
		{name: "500 origin failure", status: http.StatusInternalServerError, wantOK: false},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("content-type", "application/json")
				w.WriteHeader(test.status)
				_, _ = w.Write([]byte(`{"error":"probe"}`))
			}))
			defer server.Close()
			endpoint, err := url.Parse(server.URL + "/log?offset=0")
			if err != nil {
				t.Fatal(err)
			}
			isolation := runIsolation(context.Background(), server.Client(), runConfig{
				Endpoint: endpoint, MixedCapability: "mixed", MaxBodyBytes: 1024,
			})
			if isolation.OK != test.wantOK {
				t.Fatalf("isolation=%+v, want OK=%v", isolation, test.wantOK)
			}
			if test.status != http.StatusUnauthorized && isolation.StatusFailures != 1 {
				t.Fatalf("isolation=%+v, want one status failure", isolation)
			}
		})
	}
}

func TestCapabilityExpiryRequires401AndRejectsDataFrames(t *testing.T) {
	for _, test := range []struct {
		name        string
		status      int
		contentType string
		wantNoLeak  bool
	}{
		{name: "200 JSON", status: http.StatusOK, contentType: "application/json", wantNoLeak: false},
		{name: "401 JSON denial", status: http.StatusUnauthorized, contentType: "application/json", wantNoLeak: true},
		{name: "401 MessagePack", status: http.StatusUnauthorized, contentType: "application/msgpack", wantNoLeak: false},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method == http.MethodPost {
					w.WriteHeader(http.StatusNoContent)
					return
				}
				w.Header().Set("content-type", test.contentType)
				w.WriteHeader(test.status)
				_, _ = w.Write([]byte(`probe-body`))
			}))
			defer server.Close()
			endpoint, err := url.Parse(server.URL + "/log?offset=0")
			if err != nil {
				t.Fatal(err)
			}
			results := runInjections(context.Background(), server.Client(), runConfig{
				Endpoint: endpoint, Capability: "old-capability", MaxBodyBytes: 1024,
				InjectionURL: server.URL + "/inject", InjectionNames: []string{"capability-expiry"},
			})
			if len(results) != 1 || results[0].NoLeak != test.wantNoLeak {
				t.Fatalf("results=%+v, want one result with NoLeak=%v", results, test.wantNoLeak)
			}
		})
	}
}

func TestValidatorRejectsEarlyAndOverlappingInjections(t *testing.T) {
	tests := []struct {
		name string
		edit func(*Result)
		want string
	}{
		{
			name: "early",
			edit: func(result *Result) {
				result.Injections[0].StartedAt = result.Stages[2].StartedAt
				result.Injections[0].EndedAt = result.Stages[2].StartedAt.Add(100 * time.Millisecond)
			},
			want: "starts before reader stages ended",
		},
		{
			name: "overlap",
			edit: func(result *Result) {
				result.Injections[1].StartedAt = result.Injections[0].StartedAt
			},
			want: "overlaps or is out of order",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			result := validFixture()
			test.edit(&result)
			if err := validateResult(result, false); err == nil || !strings.Contains(err.Error(), test.want) {
				t.Fatalf("expected %q, got %v", test.want, err)
			}
		})
	}
}

func TestValidatorRejectsMalformedStageWindow(t *testing.T) {
	result := validFixture()
	result.Stages[2].EndedAt = result.Stages[2].StartedAt.Add(25 * time.Hour)
	result.EndedAt = result.Stages[2].EndedAt.Add(time.Second)
	if err := validateResult(result, false); err == nil || !strings.Contains(err.Error(), "duration is out of bounds") {
		t.Fatalf("unbounded stage was accepted: %v", err)
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
