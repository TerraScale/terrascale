package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const (
	// A result is retained evidence, rather than a process log.  Keep the
	// accepted time window finite so a forged timestamp cannot make an
	// otherwise plausible set of counters look like a reader-hour result.
	maxResultDuration    = 7 * 24 * time.Hour
	maxStageDuration     = 24 * time.Hour
	maxInjectionDuration = 24 * time.Hour
)

var requiredInjectionNames = []string{"cache-bypass", "eviction", "rule-change", "capability-expiry"}

// ValidateResult applies the production X-5 gate. It intentionally has no
// stub escape hatch: a caller that wants local inspection must say so at the
// command line or use validateResult in a package-level test.
func ValidateResult(result Result) error { return validateResult(result, false) }

// LoadResult decodes one retained evidence file without accepting trailing
// JSON values. This keeps concatenated or partially-written results invalid.
func LoadResult(reader io.Reader) (Result, error) {
	var result Result
	decoder := json.NewDecoder(io.LimitReader(reader, 64<<20))
	if err := decoder.Decode(&result); err != nil {
		return Result{}, err
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		if err == nil {
			return Result{}, errors.New("result contains trailing JSON")
		}
		return Result{}, err
	}
	return result, nil
}

// validateResult checks the production X-5 gate. Stub files can be inspected
// with allowStub, but they cannot satisfy a claim about Cloudflare behavior.
func validateResult(result Result, allowStub bool) error {
	var problems []string
	if result.SchemaVersion != resultSchemaVersion {
		problems = append(problems, fmt.Sprintf("schema_version is %d, want %d", result.SchemaVersion, resultSchemaVersion))
	}
	if result.RunID == "" || result.StartedAt.IsZero() || result.EndedAt.IsZero() {
		problems = append(problems, "missing run identity or timestamps")
	}
	derivedMode, endpointErr := validatedEndpointMode(result.Endpoint)
	if endpointErr != nil {
		problems = append(problems, "endpoint: "+endpointErr.Error())
	} else if result.Evidence.Mode != derivedMode {
		// Mode is informational only after the endpoint and its scheme have
		// been validated.  Never let a caller select production semantics by
		// putting "real-zone" in this free-form field.
		problems = append(problems, fmt.Sprintf("evidence.mode=%q does not match endpoint context %q", result.Evidence.Mode, derivedMode))
	}
	validateResultWindow(&problems, result)
	if result.WriteRate != 16 {
		problems = append(problems, "write_rate must be exactly 16")
	}
	if !allowStub && (result.Bounds.MaxConnections <= 0 || result.Bounds.MaxIdleConnections <= 0 || result.Bounds.MaxLatencySamples <= 0 || result.Bounds.MaxFrameDigests <= 0) {
		problems = append(problems, "resource bounds are missing")
	}
	if len(result.Errors) > 0 {
		problems = append(problems, fmt.Sprintf("runner reported %d errors", len(result.Errors)))
	}
	if !allowStub && derivedMode != "real-zone" {
		problems = append(problems, "production validation requires evidence.mode=real-zone")
	}
	if !result.Evidence.CredentialsUsed {
		problems = append(problems, "credentials_used is false")
	}
	if !allowStub && (!result.Evidence.TelemetryComplete || !validEvidenceURL(result.Evidence.TelemetrySource, derivedMode)) {
		problems = append(problems, "complete normalized Cloudflare telemetry is required")
	}
	if !allowStub && (!result.Evidence.InjectionsRun || !validEvidenceURL(result.Evidence.InjectionSource, derivedMode)) {
		problems = append(problems, "all fault injections must run against a credentialed control endpoint")
	}
	if result.Evidence.TelemetrySource != "" && !validEvidenceURL(result.Evidence.TelemetrySource, derivedMode) {
		problems = append(problems, "telemetry_source is malformed")
	}
	if result.Evidence.InjectionSource != "" && !validEvidenceURL(result.Evidence.InjectionSource, derivedMode) {
		problems = append(problems, "injection_source is malformed")
	}
	if len(result.Stages) != len(requiredStages) {
		problems = append(problems, fmt.Sprintf("got %d stages, want %d", len(result.Stages), len(requiredStages)))
	}
	var previous StageResult
	for i, stage := range result.Stages {
		if i >= len(requiredStages) {
			problems = append(problems, fmt.Sprintf("unexpected stage %d", i+1))
			continue
		}
		validateStage(&problems, stage, requiredStages[i], allowStub)
		if i > 0 && stage.StartedAt.Before(previous.EndedAt) {
			problems = append(problems, fmt.Sprintf("stage %d overlaps or is out of order", i+1))
		}
		previous = stage
	}
	if !allowStub {
		if len(result.Injections) != 4 {
			problems = append(problems, fmt.Sprintf("got %d injections, want 4", len(result.Injections)))
		}
		var lastInjectionEnd time.Time
		var finalStageEnd time.Time
		if len(result.Stages) > 0 {
			finalStageEnd = result.Stages[len(result.Stages)-1].EndedAt
		}
		seen := make(map[string]bool, len(result.Injections))
		for i, injection := range result.Injections {
			if seen[injection.Name] {
				problems = append(problems, "duplicate injection "+injection.Name)
			}
			seen[injection.Name] = true
			if i < len(requiredInjectionNames) && injection.Name != requiredInjectionNames[i] {
				problems = append(problems, fmt.Sprintf("injection %q is out of order; want %q", injection.Name, requiredInjectionNames[i]))
			}
			if !isRequiredInjection(injection.Name) {
				problems = append(problems, fmt.Sprintf("unknown injection %q", injection.Name))
			}
			if !injection.Requested || !injection.Observed || !injection.NoLeak || injection.Error != "" {
				problems = append(problems, fmt.Sprintf("injection %q was not observed without error or leak", injection.Name))
			}
			validateInjection(&problems, injection, result, finalStageEnd, lastInjectionEnd)
			if !injection.EndedAt.IsZero() {
				lastInjectionEnd = injection.EndedAt
			}
		}
		for _, name := range requiredInjectionNames {
			if !seen[name] {
				problems = append(problems, "missing injection "+name)
			}
		}
	}
	if len(problems) > 0 {
		return errors.New(joinProblems(problems))
	}
	return nil
}

func validateStage(problems *[]string, stage StageResult, readers int, allowStub bool) {
	prefix := fmt.Sprintf("stage %d", readers)
	if stage.Readers != readers {
		*problems = append(*problems, fmt.Sprintf("%s readers=%d", prefix, stage.Readers))
	}
	if stage.WriteRate != 16 {
		*problems = append(*problems, prefix+" write_rate is not 16")
	}
	if stage.StartedAt.IsZero() || stage.EndedAt.IsZero() {
		*problems = append(*problems, prefix+" has missing timestamps")
	} else {
		validateDuration(problems, prefix, stage.StartedAt, stage.EndedAt, stage.Duration, maxStageDuration)
	}
	if stage.Requests == 0 || stage.Successes == 0 || stage.Latency.Samples == 0 {
		*problems = append(*problems, prefix+" has no request/latency evidence")
	}
	if stage.Errors != 0 {
		*problems = append(*problems, fmt.Sprintf("%s reports %d request errors", prefix, stage.Errors))
	}
	if stage.Successes != stage.Requests {
		*problems = append(*problems, prefix+" request accounting does not balance")
	}
	if stage.Latency.Samples != stage.Requests {
		*problems = append(*problems, prefix+" latency samples do not match requests")
	}
	if !finiteNonNegative(stage.Latency.P50) || !finiteNonNegative(stage.Latency.P95) || !finiteNonNegative(stage.Latency.P99) || !finiteNonNegative(stage.Latency.Max) || stage.Latency.P95 < stage.Latency.P50 || stage.Latency.P99 < stage.Latency.P95 || stage.Latency.Max < stage.Latency.P99 {
		*problems = append(*problems, prefix+" latency percentiles are invalid")
	}
	if len(stage.Latency.Histogram) != len(latencyBucketUpperBounds) {
		if !allowStub {
			*problems = append(*problems, prefix+" latency histogram is missing or incomplete")
		}
	} else {
		var total uint64
		for i, bucket := range stage.Latency.Histogram {
			if bucket.LE != latencyBucketUpperBounds[i] || (i > 0 && bucket.Count < stage.Latency.Histogram[i-1].Count) {
				*problems = append(*problems, prefix+" latency histogram buckets are invalid")
				break
			}
			total = bucket.Count
		}
		if total != stage.Latency.Samples {
			*problems = append(*problems, prefix+" latency histogram does not match samples")
		}
	}
	if !stage.OrderedBytes.OK || stage.OrderedBytes.Responses == 0 || stage.OrderedBytes.Responses > stage.Successes || stage.OrderedBytes.Violations != 0 {
		*problems = append(*problems, prefix+" did not prove ordered byte equality")
	}
	if !stage.Isolation.OK || stage.Isolation.Attempts == 0 || stage.Isolation.Denied == 0 || !isolationAccountingBalanced(stage.Isolation) || stage.Isolation.LeakedBytes != 0 || stage.Isolation.AuthCrossover {
		*problems = append(*problems, prefix+" did not prove capability isolation")
	}
	if !allowStub && stage.Isolation.MixedCredentialStatus != http.StatusUnauthorized {
		*problems = append(*problems, prefix+" mixed credential was not denied")
	}
	if stage.Worker == nil {
		if !allowStub {
			*problems = append(*problems, prefix+" has no Worker request metric")
		}
	} else if *stage.Worker != 0 {
		*problems = append(*problems, fmt.Sprintf("%s Worker requests=%d, want 0", prefix, *stage.Worker))
	}
	if stage.CostPerMillion == nil || !finiteNonNegativePtr(stage.CostPerMillion) {
		if !allowStub {
			*problems = append(*problems, prefix+" has no measured reader-hour cost")
		}
	}
	if stage.CostPerMillion != nil && !finiteNonNegativePtr(stage.CostPerMillion) {
		*problems = append(*problems, prefix+" reader-hour cost is malformed")
	}
	if allowStub {
		validateOptionalCache(problems, prefix, stage.Cache)
		return
	}
	if stage.Cache.EdgeMisses == nil || stage.Cache.UpperTierMisses == nil || stage.Cache.OriginFetches == nil || stage.Cache.TotalBytes == nil || stage.Cache.CacheHitBytes == nil || stage.Cache.ByteHitRatio == nil {
		*problems = append(*problems, prefix+" has incomplete cache/origin telemetry")
		return
	}
	if *stage.Cache.UpperTierMisses > *stage.Cache.EdgeMisses || *stage.Cache.OriginFetches > *stage.Cache.UpperTierMisses || *stage.Cache.EdgeMisses > stage.Requests || *stage.Cache.CacheHitBytes > *stage.Cache.TotalBytes || !finiteRatio(stage.Cache.ByteHitRatio) {
		*problems = append(*problems, prefix+" cache byte-hit ratio is invalid")
	}
	wantRatio := 0.0
	if *stage.Cache.TotalBytes > 0 {
		wantRatio = float64(*stage.Cache.CacheHitBytes) / float64(*stage.Cache.TotalBytes)
	}
	if math.Abs(*stage.Cache.ByteHitRatio-wantRatio) > 0.000000001 {
		*problems = append(*problems, prefix+" cache byte-hit ratio does not match byte counters")
	}
}

func finiteNonNegative(value float64) bool {
	return !math.IsNaN(value) && !math.IsInf(value, 0) && value >= 0
}

func finiteNonNegativePtr(value *float64) bool { return value != nil && finiteNonNegative(*value) }

func finiteRatio(value *float64) bool {
	return value != nil && !math.IsNaN(*value) && !math.IsInf(*value, 0) && *value >= 0 && *value <= 1
}

func validateOptionalCache(problems *[]string, prefix string, cache CacheMetrics) {
	present := cache.EdgeMisses != nil || cache.UpperTierMisses != nil || cache.OriginFetches != nil || cache.TotalBytes != nil || cache.CacheHitBytes != nil || cache.ByteHitRatio != nil
	if !present {
		return
	}
	if cache.EdgeMisses == nil || cache.UpperTierMisses == nil || cache.OriginFetches == nil || cache.TotalBytes == nil || cache.CacheHitBytes == nil || cache.ByteHitRatio == nil {
		*problems = append(*problems, prefix+" has incomplete cache/origin telemetry")
		return
	}
	if *cache.UpperTierMisses > *cache.EdgeMisses || *cache.OriginFetches > *cache.UpperTierMisses || *cache.CacheHitBytes > *cache.TotalBytes || !finiteRatio(cache.ByteHitRatio) {
		*problems = append(*problems, prefix+" cache metrics are malformed")
		return
	}
	wantRatio := 0.0
	if *cache.TotalBytes > 0 {
		wantRatio = float64(*cache.CacheHitBytes) / float64(*cache.TotalBytes)
	}
	if math.Abs(*cache.ByteHitRatio-wantRatio) > 0.000000001 {
		*problems = append(*problems, prefix+" cache byte-hit ratio does not match byte counters")
	}
}

func validateDuration(problems *[]string, prefix string, start, end time.Time, duration float64, maximum time.Duration) {
	if !start.Before(end) {
		*problems = append(*problems, prefix+" timestamps are not in increasing order")
		return
	}
	expected := end.Sub(start)
	if expected <= 0 || expected > maximum || math.IsNaN(duration) || math.IsInf(duration, 0) || duration <= 0 || duration > maximum.Seconds() {
		*problems = append(*problems, prefix+" duration is out of bounds")
		return
	}
	// Duration is serialized as seconds while timestamps retain nanoseconds.
	if math.Abs(duration-expected.Seconds()) > 0.000001 {
		*problems = append(*problems, prefix+" duration does not match timestamps")
	}
}

func validateResultWindow(problems *[]string, result Result) {
	if result.StartedAt.IsZero() || result.EndedAt.IsZero() {
		return
	}
	validateDuration(problems, "result", result.StartedAt, result.EndedAt, result.EndedAt.Sub(result.StartedAt).Seconds(), maxResultDuration)
	for i, stage := range result.Stages {
		if stage.StartedAt.IsZero() || stage.EndedAt.IsZero() {
			continue
		}
		if stage.StartedAt.Before(result.StartedAt) || stage.EndedAt.After(result.EndedAt) {
			*problems = append(*problems, fmt.Sprintf("stage %d is outside result timestamps", i+1))
		}
	}
}

func validateInjection(problems *[]string, injection InjectionTest, result Result, notBefore, previousEnd time.Time) {
	prefix := fmt.Sprintf("injection %q", injection.Name)
	if injection.Name == "" || injection.StartedAt.IsZero() || injection.EndedAt.IsZero() {
		*problems = append(*problems, prefix+" has missing identity or timestamps")
		return
	}
	validateDuration(problems, prefix, injection.StartedAt, injection.EndedAt, injection.EndedAt.Sub(injection.StartedAt).Seconds(), maxInjectionDuration)
	if injection.StartedAt.Before(result.StartedAt) || injection.EndedAt.After(result.EndedAt) {
		*problems = append(*problems, prefix+" is outside result timestamps")
	}
	if !notBefore.IsZero() && injection.StartedAt.Before(notBefore) {
		*problems = append(*problems, prefix+" starts before reader stages ended")
	}
	if !previousEnd.IsZero() && injection.StartedAt.Before(previousEnd) {
		*problems = append(*problems, prefix+" overlaps or is out of order")
	}
	if injection.ProbeStatus < 100 || injection.ProbeStatus > 599 {
		*problems = append(*problems, prefix+" has malformed probe status")
	}
	if injection.Name == "capability-expiry" && injection.ProbeStatus != http.StatusUnauthorized {
		*problems = append(*problems, prefix+" must prove a 401 denial")
	}
}

func isolationAccountingBalanced(isolation Isolation) bool {
	if isolation.Denied > isolation.Attempts || isolation.StatusFailures > isolation.Attempts-isolation.Denied {
		return false
	}
	return isolation.Denied+isolation.StatusFailures == isolation.Attempts
}

func isRequiredInjection(name string) bool {
	switch name {
	case "cache-bypass", "eviction", "rule-change", "capability-expiry":
		return true
	default:
		return false
	}
}

func validatedEndpointMode(raw string) (string, error) {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || u.Scheme == "" || u.Host == "" || u.Hostname() == "" || u.Path == "" {
		return "", errors.New("endpoint must be an absolute URL containing a path")
	}
	if u.User != nil || u.Fragment != "" {
		return "", errors.New("endpoint userinfo or fragment is not allowed")
	}
	// Loopback is always local evidence, even when a developer wraps a stub
	// in TLS. Never allow HTTPS localhost to masquerade as a production run.
	if isLoopback(strings.ToLower(u.Hostname())) {
		return "stub", nil
	}
	switch strings.ToLower(u.Scheme) {
	case "https":
		return "real-zone", nil
	}
	return "", errors.New("endpoint must use HTTPS; HTTP is allowed only for a loopback stub")
}

func validEvidenceURL(raw, mode string) bool {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || u.Scheme == "" || u.Host == "" || u.User != nil || u.Fragment != "" {
		return false
	}
	if mode == "real-zone" {
		return strings.EqualFold(u.Scheme, "https")
	}
	return (strings.EqualFold(u.Scheme, "https")) || (strings.EqualFold(u.Scheme, "http") && isLoopback(u.Hostname()))
}

func joinProblems(problems []string) string {
	result := "invalid X-5 result:"
	for _, problem := range problems {
		result += " " + problem + ";"
	}
	return result
}
