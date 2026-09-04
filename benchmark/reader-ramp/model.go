package main

import "time"

const resultSchemaVersion = 1

var requiredStages = []int{10_000, 100_000, 1_000_000}

// Result is the evidence file written by the X-5 runner. A nil pointer is
// intentional: the runner did not observe that value, so the validator must
// reject the file for a production claim rather than filling in a guess.
type Result struct {
	SchemaVersion int             `json:"schema_version"`
	RunID         string          `json:"run_id"`
	Endpoint      string          `json:"endpoint"`
	WriteRate     int             `json:"write_rate"`
	StartedAt     time.Time       `json:"started_at"`
	EndedAt       time.Time       `json:"ended_at"`
	Evidence      Evidence        `json:"evidence"`
	Stages        []StageResult   `json:"stages"`
	Injections    []InjectionTest `json:"injections"`
	Errors        []string        `json:"errors,omitempty"`
}

type Evidence struct {
	Mode              string `json:"mode"` // real-zone or stub
	CredentialsUsed   bool   `json:"credentials_used"`
	TelemetrySource   string `json:"telemetry_source,omitempty"`
	InjectionSource   string `json:"injection_source,omitempty"`
	TelemetryComplete bool   `json:"telemetry_complete"`
	InjectionsRun     bool   `json:"injections_run"`
}

type StageResult struct {
	Readers        int          `json:"readers"`
	WriteRate      int          `json:"write_rate"`
	StartedAt      time.Time    `json:"started_at"`
	EndedAt        time.Time    `json:"ended_at"`
	Duration       float64      `json:"duration_seconds"`
	Requests       uint64       `json:"requests"`
	Successes      uint64       `json:"successes"`
	Errors         uint64       `json:"errors"`
	Latency        Latency      `json:"latency_ms"`
	Cache          CacheMetrics `json:"cache"`
	Worker         *uint64      `json:"worker_requests"`
	CostPerMillion *float64     `json:"cost_per_million_reader_hours_usd"`
	OrderedBytes   OrderedBytes `json:"ordered_bytes"`
	Isolation      Isolation    `json:"capability_isolation"`
	TelemetryNote  string       `json:"telemetry_note,omitempty"`
}

type Latency struct {
	Samples uint64  `json:"samples"`
	P50     float64 `json:"p50"`
	P95     float64 `json:"p95"`
	P99     float64 `json:"p99"`
	Max     float64 `json:"max"`
}

type CacheMetrics struct {
	EdgeMisses      *uint64  `json:"edge_misses"`
	UpperTierMisses *uint64  `json:"upper_tier_misses"`
	OriginFetches   *uint64  `json:"origin_fetches"`
	TotalBytes      *uint64  `json:"total_bytes"`
	CacheHitBytes   *uint64  `json:"cache_hit_bytes"`
	ByteHitRatio    *float64 `json:"byte_hit_ratio"`
}

type OrderedBytes struct {
	OK         bool   `json:"ok"`
	Responses  uint64 `json:"responses"`
	Violations uint64 `json:"violations"`
}

type Isolation struct {
	OK             bool   `json:"ok"`
	Attempts       uint64 `json:"attempts"`
	Denied         uint64 `json:"denied"`
	LeakedBytes    uint64 `json:"leaked_bytes"`
	StatusFailures uint64 `json:"status_failures"`
}

type InjectionTest struct {
	Name        string    `json:"name"`
	Requested   bool      `json:"requested"`
	Observed    bool      `json:"observed"`
	ProbeStatus int       `json:"probe_status"`
	ProbeBytes  uint64    `json:"probe_bytes"`
	NoLeak      bool      `json:"no_leak"`
	StartedAt   time.Time `json:"started_at"`
	EndedAt     time.Time `json:"ended_at"`
	Error       string    `json:"error,omitempty"`
}

type telemetrySnapshot struct {
	EdgeMisses      *uint64  `json:"edge_misses"`
	UpperTierMisses *uint64  `json:"upper_tier_misses"`
	OriginFetches   *uint64  `json:"origin_fetches"`
	TotalBytes      *uint64  `json:"total_bytes"`
	CacheHitBytes   *uint64  `json:"cache_hit_bytes"`
	WorkerRequests  *uint64  `json:"worker_requests"`
	OriginFillCost  *float64 `json:"origin_fill_cost_usd"`
}
