package main

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

const (
	capabilityHeader  = "X-TerraScale-Capability"
	maxLatencySamples = 1_000_000
)

type runConfig struct {
	Endpoint        *url.URL
	Capability      string
	MixedCapability string
	TelemetryURL    string
	TelemetryToken  string
	InjectionURL    string
	InjectionToken  string
	Readers         []int
	Duration        time.Duration
	WriteRate       int
	MaxBodyBytes    int64
	InjectionNames  []string
}

type stageCounters struct {
	requests, successes, failures atomic.Uint64
	latencyMu                     sync.Mutex
	latencies                     []float64
	latencyTotal                  atomic.Uint64
	orderedMu                     sync.Mutex
	frames                        map[int64][32]byte
	orderedViolations             atomic.Uint64
	orderedResponses              atomic.Uint64
	firstError                    atomic.Value
}

func runRamp(ctx context.Context, cfg runConfig) (Result, error) {
	if len(cfg.Readers) == 0 {
		return Result{}, errors.New("at least one reader stage is required")
	}
	if cfg.Endpoint == nil || strings.TrimSpace(cfg.Capability) == "" {
		return Result{}, errors.New("endpoint and capability are required")
	}
	if cfg.MaxBodyBytes <= 0 {
		return Result{}, errors.New("max body size must be positive")
	}
	client := &http.Client{
		Transport: &http.Transport{
			MaxIdleConns:        1_000_000,
			MaxIdleConnsPerHost: 1_000_000,
			MaxConnsPerHost:     0,
			IdleConnTimeout:     90 * time.Second,
		},
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return errors.New("redirects are refused; a ramp must stay on the configured zone")
		},
	}
	result := Result{
		SchemaVersion: resultSchemaVersion,
		RunID:         fmt.Sprintf("%d", time.Now().UnixNano()),
		Endpoint:      cfg.Endpoint.String(),
		WriteRate:     cfg.WriteRate,
		StartedAt:     time.Now().UTC(),
		Evidence: Evidence{
			Mode:            modeForEndpoint(cfg.Endpoint),
			CredentialsUsed: strings.TrimSpace(cfg.Capability) != "" && (cfg.TelemetryToken != "" || cfg.Endpoint.Scheme == "http"),
			TelemetrySource: cfg.TelemetryURL,
			InjectionSource: cfg.InjectionURL,
		},
	}
	for _, readers := range cfg.Readers {
		stage, err := runStage(ctx, client, cfg, readers)
		result.Stages = append(result.Stages, stage)
		if err != nil {
			result.Errors = append(result.Errors, fmt.Sprintf("%d readers: %v", readers, err))
		}
	}
	if len(cfg.InjectionNames) > 0 {
		result.Injections = runInjections(ctx, client, cfg)
		result.Evidence.InjectionsRun = true
	}
	result.Evidence.TelemetryComplete = allTelemetryPresent(result.Stages)
	result.EndedAt = time.Now().UTC()
	return result, nil
}

func modeForEndpoint(endpoint *url.URL) string {
	if endpoint.Scheme == "http" && isLoopback(endpoint.Hostname()) {
		return "stub"
	}
	return "real-zone"
}

func runStage(parent context.Context, client *http.Client, cfg runConfig, readers int) (StageResult, error) {
	stage := StageResult{Readers: readers, WriteRate: cfg.WriteRate, StartedAt: time.Now().UTC()}
	ctx, cancel := context.WithTimeout(parent, cfg.Duration)
	defer cancel()
	counters := &stageCounters{frames: make(map[int64][32]byte)}
	var before telemetrySnapshot
	telemetryBeforeErr := error(nil)
	if cfg.TelemetryURL != "" {
		before, telemetryBeforeErr = fetchTelemetry(ctx, client, cfg.TelemetryURL, cfg.TelemetryToken, stage.StartedAt)
	}
	var wg sync.WaitGroup
	wg.Add(readers)
	for i := 0; i < readers; i++ {
		go func() {
			defer wg.Done()
			runReader(ctx, client, cfg, counters)
		}()
	}
	wg.Wait()
	stage.EndedAt = time.Now().UTC()
	stage.Duration = stage.EndedAt.Sub(stage.StartedAt).Seconds()
	stage.Requests = counters.requests.Load()
	stage.Successes = counters.successes.Load()
	stage.Errors = counters.failures.Load()
	stage.Latency = summarizeLatency(counters)
	stage.OrderedBytes = OrderedBytes{
		OK:        counters.orderedViolations.Load() == 0 && counters.orderedResponses.Load() > 0,
		Responses: counters.orderedResponses.Load(), Violations: counters.orderedViolations.Load(),
	}
	if value := counters.firstError.Load(); value != nil {
		return stage, errors.New(value.(string))
	}
	if stage.Requests == 0 {
		return stage, errors.New("stage made no requests")
	}
	if cfg.TelemetryURL == "" {
		stage.TelemetryNote = "telemetry unavailable: --telemetry-url was not configured"
		isolationCtx, cancelIsolation := context.WithTimeout(parent, 10*time.Second)
		stage.Isolation = runIsolation(isolationCtx, client, cfg)
		cancelIsolation()
		return stage, nil
	}
	if telemetryBeforeErr != nil {
		stage.TelemetryNote = "telemetry before stage: " + telemetryBeforeErr.Error()
		isolationCtx, cancelIsolation := context.WithTimeout(parent, 10*time.Second)
		stage.Isolation = runIsolation(isolationCtx, client, cfg)
		cancelIsolation()
		return stage, nil
	}
	after, err := fetchTelemetry(context.Background(), client, cfg.TelemetryURL, cfg.TelemetryToken, stage.EndedAt)
	if err != nil {
		stage.TelemetryNote = "telemetry after stage: " + err.Error()
		isolationCtx, cancelIsolation := context.WithTimeout(parent, 10*time.Second)
		stage.Isolation = runIsolation(isolationCtx, client, cfg)
		cancelIsolation()
		return stage, nil
	}
	if err := applyTelemetry(&stage, before, after); err != nil {
		stage.TelemetryNote = err.Error()
	}
	isolationCtx, cancelIsolation := context.WithTimeout(parent, 10*time.Second)
	stage.Isolation = runIsolation(isolationCtx, client, cfg)
	cancelIsolation()
	return stage, nil
}

func runIsolation(ctx context.Context, client *http.Client, cfg runConfig) Isolation {
	status, body, dataBearing, err := probe(ctx, client, cfg.Endpoint, cfg.MixedCapability, cfg.MaxBodyBytes)
	isolation := Isolation{Attempts: 1}
	if err != nil {
		isolation.StatusFailures = 1
		return isolation
	}
	if status < 200 || status >= 300 {
		isolation.Denied = 1
	}
	if dataBearing {
		isolation.LeakedBytes = uint64(len(body))
	}
	isolation.OK = isolation.Denied == 1 && isolation.LeakedBytes == 0
	return isolation
}

func runReader(ctx context.Context, client *http.Client, cfg runConfig, counters *stageCounters) {
	offset, err := initialOffset(cfg.Endpoint)
	if err != nil {
		counters.failures.Add(1)
		counters.firstError.CompareAndSwap(nil, err.Error())
		return
	}
	lastRevision := offset
	for ctx.Err() == nil {
		started := time.Now()
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, withOffset(cfg.Endpoint, offset), nil)
		if err != nil {
			counters.failures.Add(1)
			counters.firstError.CompareAndSwap(nil, err.Error())
			return
		}
		req.Header.Set(capabilityHeader, cfg.Capability)
		req.Header.Set("Accept", "application/msgpack")
		counters.requests.Add(1)
		resp, err := client.Do(req)
		if err != nil {
			counters.failures.Add(1)
			if ctx.Err() == nil {
				counters.firstError.CompareAndSwap(nil, err.Error())
			}
			return
		}
		body, readErr := io.ReadAll(io.LimitReader(resp.Body, cfg.MaxBodyBytes+1))
		_ = resp.Body.Close()
		elapsed := float64(time.Since(started).Microseconds()) / 1000
		counters.latencyMu.Lock()
		seen := counters.latencyTotal.Load()
		if len(counters.latencies) < maxLatencySamples {
			counters.latencies = append(counters.latencies, elapsed)
		} else {
			// Keep a bounded deterministic sample. The total remains exact,
			// while a million readers cannot turn the result into an unbounded
			// allocation.
			counters.latencies[seen%maxLatencySamples] = elapsed
		}
		counters.latencyTotal.Add(1)
		counters.latencyMu.Unlock()
		if readErr != nil {
			counters.failures.Add(1)
			if ctx.Err() == nil {
				counters.firstError.CompareAndSwap(nil, readErr.Error())
			}
			return
		}
		if int64(len(body)) > cfg.MaxBodyBytes {
			counters.failures.Add(1)
			counters.firstError.CompareAndSwap(nil, "response exceeded --max-body-bytes")
			return
		}
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			counters.failures.Add(1)
			if resp.StatusCode == http.StatusGone {
				// Retention reset is a typed response. Stop this reader; a fresh
				// attach is required and must not be guessed by the benchmark.
				return
			}
			continue
		}
		counters.successes.Add(1)
		revision, frameErr := frameRevision(body)
		if frameErr != nil {
			counters.failures.Add(1)
			counters.firstError.CompareAndSwap(nil, frameErr.Error())
			return
		}
		if revision == 0 {
			continue // no-store long-poll timeout, not a data-bearing frame
		}
		if revision <= lastRevision {
			counters.orderedViolations.Add(1)
			return
		}
		lastRevision = revision
		digest := sha256.Sum256(body)
		counters.orderedResponses.Add(1)
		counters.orderedMu.Lock()
		if previous, ok := counters.frames[revision]; ok && previous != digest {
			counters.orderedViolations.Add(1)
		} else {
			counters.frames[revision] = digest
		}
		counters.orderedMu.Unlock()
		offset = revision
	}
}

func initialOffset(endpoint *url.URL) (int64, error) {
	raw := endpoint.Query().Get("offset")
	if raw == "" {
		return 0, nil
	}
	offset, err := strconv.ParseInt(raw, 10, 64)
	if err != nil || offset < 0 {
		return 0, fmt.Errorf("endpoint offset must be a non-negative integer, got %q", raw)
	}
	return offset, nil
}

func withOffset(endpoint *url.URL, offset int64) string {
	copyURL := *endpoint
	query := copyURL.Query()
	query.Set("offset", strconv.FormatInt(offset, 10))
	copyURL.RawQuery = query.Encode()
	return copyURL.String()
}

func summarizeLatency(counters *stageCounters) Latency {
	counters.latencyMu.Lock()
	values := append([]float64(nil), counters.latencies...)
	counters.latencyMu.Unlock()
	if len(values) == 0 {
		return Latency{}
	}
	sort.Float64s(values)
	percentile := func(p float64) float64 {
		index := int(p*float64(len(values)-1) + 0.5)
		return values[index]
	}
	return Latency{Samples: counters.latencyTotal.Load(), P50: percentile(.50), P95: percentile(.95), P99: percentile(.99), Max: values[len(values)-1]}
}

func allTelemetryPresent(stages []StageResult) bool {
	if len(stages) == 0 {
		return false
	}
	for _, stage := range stages {
		if stage.Worker == nil || stage.CostPerMillion == nil || stage.Cache.EdgeMisses == nil || stage.Cache.UpperTierMisses == nil || stage.Cache.OriginFetches == nil || stage.Cache.TotalBytes == nil || stage.Cache.CacheHitBytes == nil || stage.Cache.ByteHitRatio == nil {
			return false
		}
	}
	return true
}

func fetchTelemetry(ctx context.Context, client *http.Client, rawURL, token string, at time.Time) (telemetrySnapshot, error) {
	u, err := url.Parse(rawURL)
	if err != nil || u.Scheme == "" || u.Host == "" {
		return telemetrySnapshot{}, errors.New("telemetry URL must be absolute")
	}
	query := u.Query()
	query.Set("at", at.UTC().Format(time.RFC3339Nano))
	u.RawQuery = query.Encode()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return telemetrySnapshot{}, err
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := client.Do(req)
	if err != nil {
		return telemetrySnapshot{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return telemetrySnapshot{}, fmt.Errorf("status %d", resp.StatusCode)
	}
	var snapshot telemetrySnapshot
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&snapshot); err != nil {
		return telemetrySnapshot{}, err
	}
	return snapshot, nil
}

func applyTelemetry(stage *StageResult, before, after telemetrySnapshot) error {
	delta := func(name string, a, b *uint64) (*uint64, error) {
		if a == nil || b == nil {
			return nil, fmt.Errorf("telemetry field %s is missing", name)
		}
		if *b < *a {
			return nil, fmt.Errorf("telemetry field %s decreased", name)
		}
		value := *b - *a
		return &value, nil
	}
	var err error
	if stage.Cache.EdgeMisses, err = delta("edge_misses", before.EdgeMisses, after.EdgeMisses); err != nil {
		return err
	}
	if stage.Cache.UpperTierMisses, err = delta("upper_tier_misses", before.UpperTierMisses, after.UpperTierMisses); err != nil {
		return err
	}
	if stage.Cache.OriginFetches, err = delta("origin_fetches", before.OriginFetches, after.OriginFetches); err != nil {
		return err
	}
	if stage.Cache.TotalBytes, err = delta("total_bytes", before.TotalBytes, after.TotalBytes); err != nil {
		return err
	}
	if stage.Cache.CacheHitBytes, err = delta("cache_hit_bytes", before.CacheHitBytes, after.CacheHitBytes); err != nil {
		return err
	}
	if *stage.Cache.CacheHitBytes > *stage.Cache.TotalBytes {
		return errors.New("cache_hit_bytes exceeds total_bytes")
	}
	ratio := 0.0
	if *stage.Cache.TotalBytes > 0 {
		ratio = float64(*stage.Cache.CacheHitBytes) / float64(*stage.Cache.TotalBytes)
	}
	stage.Cache.ByteHitRatio = &ratio
	if stage.Worker, err = delta("worker_requests", before.WorkerRequests, after.WorkerRequests); err != nil {
		return err
	}
	if before.OriginFillCost == nil || after.OriginFillCost == nil {
		return errors.New("telemetry field origin_fill_cost_usd is missing")
	}
	if *after.OriginFillCost < *before.OriginFillCost {
		return errors.New("telemetry field origin_fill_cost_usd decreased")
	}
	hours := stage.Duration * float64(stage.Readers) / 3600
	if hours <= 0 {
		return errors.New("stage duration is not positive")
	}
	cost := (*after.OriginFillCost - *before.OriginFillCost) / hours * 1_000_000
	stage.CostPerMillion = &cost
	return nil
}

func runInjections(ctx context.Context, client *http.Client, cfg runConfig) []InjectionTest {
	results := make([]InjectionTest, 0, len(cfg.InjectionNames))
	for _, name := range cfg.InjectionNames {
		result := InjectionTest{Name: name, Requested: true, StartedAt: time.Now().UTC()}
		err := postInjection(ctx, client, cfg, name)
		if err != nil {
			result.Error = err.Error()
			result.EndedAt = time.Now().UTC()
			results = append(results, result)
			continue
		}
		result.Observed = true
		status, body, dataBearing, probeErr := probe(ctx, client, cfg.Endpoint, cfg.Capability, cfg.MaxBodyBytes)
		result.ProbeStatus, result.ProbeBytes = status, uint64(len(body))
		result.NoLeak = probeErr == nil
		if probeErr != nil {
			result.Error = probeErr.Error()
		}
		// A capability-expiry injection must deny the old capability. The
		// endpoint reports the expected transition; a successful old-cap read
		// is evidence that the injection did not take effect.
		if name == "capability-expiry" && dataBearing {
			result.NoLeak = false
			result.Error = "expired capability still returned bytes"
		}
		result.EndedAt = time.Now().UTC()
		results = append(results, result)
	}
	return results
}

func postInjection(ctx context.Context, client *http.Client, cfg runConfig, name string) error {
	body, err := json.Marshal(map[string]string{"scenario": name, "endpoint": cfg.Endpoint.String()})
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, cfg.InjectionURL, strings.NewReader(string(body)))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	if cfg.InjectionToken != "" {
		req.Header.Set("Authorization", "Bearer "+cfg.InjectionToken)
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if _, err := io.Copy(io.Discard, io.LimitReader(resp.Body, 1<<20)); err != nil {
		return err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("injection %q status %d", name, resp.StatusCode)
	}
	return nil
}

func probe(ctx context.Context, client *http.Client, endpoint *url.URL, capability string, maxBody int64) (int, []byte, bool, error) {
	offset, err := initialOffset(endpoint)
	if err != nil {
		return 0, nil, false, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, withOffset(endpoint, offset), nil)
	if err != nil {
		return 0, nil, false, err
	}
	req.Header.Set(capabilityHeader, capability)
	resp, err := client.Do(req)
	if err != nil {
		return 0, nil, false, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBody+1))
	if err != nil {
		return resp.StatusCode, nil, false, err
	}
	if int64(len(body)) > maxBody {
		return resp.StatusCode, body, false, errors.New("probe response exceeded max body")
	}
	dataBearing := resp.StatusCode >= 200 && resp.StatusCode < 300 && strings.Contains(strings.ToLower(resp.Header.Get("Content-Type")), "application/msgpack")
	return resp.StatusCode, body, dataBearing, nil
}

// frameRevision decodes only the deterministic map header needed by the
// benchmark. It intentionally rejects unknown MessagePack shapes rather than
// treating arbitrary bytes as ordered data.
func frameRevision(data []byte) (int64, error) {
	reader := msgpackReader{data: data}
	count, err := reader.mapLen()
	if err != nil {
		return 0, err
	}
	var revision int64
	found := false
	for i := 0; i < count; i++ {
		key, err := reader.string()
		if err != nil {
			return 0, err
		}
		if key == "revision" {
			revision, err = reader.revisionValue()
			if err != nil {
				return 0, err
			}
			found = true
		} else if err := reader.skip(); err != nil {
			return 0, err
		}
	}
	if !found {
		return 0, errors.New("MessagePack frame has no revision")
	}
	return revision, nil
}

func (r *msgpackReader) revisionValue() (int64, error) {
	if r.pos >= len(r.data) {
		return 0, errors.New("truncated MessagePack revision")
	}
	marker := r.data[r.pos]
	if marker >= 0xa0 && marker <= 0xbf || marker == 0xd9 || marker == 0xda {
		value, err := r.string()
		if err != nil {
			return 0, err
		}
		revision, err := strconv.ParseInt(value, 10, 64)
		if err != nil || revision < 0 {
			return 0, errors.New("MessagePack revision string is invalid")
		}
		return revision, nil
	}
	return r.integer()
}

type msgpackReader struct {
	data []byte
	pos  int
}

func (r *msgpackReader) take(n int) ([]byte, error) {
	if n < 0 || r.pos > len(r.data)-n {
		return nil, errors.New("truncated MessagePack frame")
	}
	b := r.data[r.pos : r.pos+n]
	r.pos += n
	return b, nil
}
func (r *msgpackReader) byte() (byte, error) {
	b, err := r.take(1)
	if err != nil {
		return 0, err
	}
	return b[0], nil
}
func (r *msgpackReader) mapLen() (int, error) {
	b, err := r.byte()
	if err != nil {
		return 0, err
	}
	switch {
	case b >= 0x80 && b <= 0x8f:
		return int(b - 0x80), nil
	case b == 0xde:
		x, e := r.take(2)
		return int(uint16(x[0])<<8 | uint16(x[1])), e
	case b == 0xdf:
		return 0, errors.New("oversized MessagePack map")
	}
	return 0, errors.New("MessagePack frame is not a map")
}
func (r *msgpackReader) string() (string, error) {
	b, err := r.byte()
	if err != nil {
		return "", err
	}
	var n int
	switch {
	case b >= 0xa0 && b <= 0xbf:
		n = int(b - 0xa0)
	case b == 0xd9:
		x, e := r.byte()
		if e != nil {
			return "", e
		}
		n = int(x)
	case b == 0xda:
		x, e := r.take(2)
		if e != nil {
			return "", e
		}
		n = int(uint16(x[0])<<8 | uint16(x[1]))
	default:
		return "", errors.New("MessagePack key is not a string")
	}
	value, err := r.take(n)
	return string(value), err
}
func (r *msgpackReader) integer() (int64, error) {
	b, err := r.byte()
	if err != nil {
		return 0, err
	}
	if b <= 0x7f {
		return int64(b), nil
	}
	if b >= 0xe0 {
		return int64(int8(b)), nil
	}
	sizes := map[byte]int{0xcc: 1, 0xcd: 2, 0xce: 4, 0xcf: 8, 0xd0: 1, 0xd1: 2, 0xd2: 4, 0xd3: 8}
	n, ok := sizes[b]
	if !ok {
		return 0, errors.New("MessagePack revision is not an integer")
	}
	x, err := r.take(n)
	if err != nil {
		return 0, err
	}
	switch b {
	case 0xcc:
		return int64(x[0]), nil
	case 0xcd:
		return int64(uint16(x[0])<<8 | uint16(x[1])), nil
	case 0xce:
		return int64(uint32(x[0])<<24 | uint32(x[1])<<16 | uint32(x[2])<<8 | uint32(x[3])), nil
	case 0xcf:
		return int64(uint64(x[0])<<56 | uint64(x[1])<<48 | uint64(x[2])<<40 | uint64(x[3])<<32 | uint64(x[4])<<24 | uint64(x[5])<<16 | uint64(x[6])<<8 | uint64(x[7])), nil
	case 0xd0:
		return int64(int8(x[0])), nil
	case 0xd1:
		return int64(int16(uint16(x[0])<<8 | uint16(x[1]))), nil
	case 0xd2:
		return int64(int32(uint32(x[0])<<24 | uint32(x[1])<<16 | uint32(x[2])<<8 | uint32(x[3]))), nil
	default:
		return int64(int64(uint64(x[0])<<56 | uint64(x[1])<<48 | uint64(x[2])<<40 | uint64(x[3])<<32 | uint64(x[4])<<24 | uint64(x[5])<<16 | uint64(x[6])<<8 | uint64(x[7]))), nil
	}
}
func (r *msgpackReader) skip() error {
	b, err := r.byte()
	if err != nil {
		return err
	}
	switch {
	case b <= 0x7f || b >= 0xe0 || b == 0xc0 || b == 0xc2 || b == 0xc3:
		return nil
	case b >= 0x90 && b <= 0x9f:
		for i := 0; i < int(b-0x90); i++ {
			if err := r.skip(); err != nil {
				return err
			}
		}
		return nil
	case b >= 0x80 && b <= 0x8f:
		for i := 0; i < int(b-0x80)*2; i++ {
			if err := r.skip(); err != nil {
				return err
			}
		}
		return nil
	case b >= 0xa0 && b <= 0xbf:
		_, err = r.take(int(b - 0xa0))
		return err
	case b == 0xcc || b == 0xd0:
		_, err = r.take(1)
		return err
	case b == 0xcd || b == 0xd1:
		_, err = r.take(2)
		return err
	case b == 0xce || b == 0xd2:
		_, err = r.take(4)
		return err
	case b == 0xcf || b == 0xd3:
		_, err = r.take(8)
		return err
	case b == 0xd9:
		n, e := r.byte()
		if e != nil {
			return e
		}
		_, err = r.take(int(n))
		return err
	case b == 0xda:
		n, e := r.take(2)
		if e != nil {
			return e
		}
		_, err = r.take(int(uint16(n[0])<<8 | uint16(n[1])))
		return err
	case b == 0xc4:
		n, e := r.byte()
		if e != nil {
			return e
		}
		_, err = r.take(int(n))
		return err
	case b == 0xc5:
		n, e := r.take(2)
		if e != nil {
			return e
		}
		_, err = r.take(int(uint16(n[0])<<8 | uint16(n[1])))
		return err
	case b == 0xc6:
		n, e := r.take(4)
		if e != nil {
			return e
		}
		_, err = r.take(int(uint32(n[0])<<24 | uint32(n[1])<<16 | uint32(n[2])<<8 | uint32(n[3])))
		return err
	case b == 0xdc:
		n, e := r.take(2)
		if e != nil {
			return e
		}
		for i := 0; i < int(uint16(n[0])<<8|uint16(n[1])); i++ {
			if err := r.skip(); err != nil {
				return err
			}
		}
		return nil
	case b == 0xde:
		n, e := r.take(2)
		if e != nil {
			return e
		}
		for i := 0; i < int(uint16(n[0])<<8|uint16(n[1]))*2; i++ {
			if err := r.skip(); err != nil {
				return err
			}
		}
		return nil
	}
	return errors.New("unsupported MessagePack value")
}
