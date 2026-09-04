package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
)

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
	if result.RunID == "" || result.Endpoint == "" || result.StartedAt.IsZero() || result.EndedAt.IsZero() {
		problems = append(problems, "missing run identity or timestamps")
	}
	if result.WriteRate != 16 {
		problems = append(problems, "write_rate must be exactly 16")
	}
	if len(result.Errors) > 0 {
		problems = append(problems, fmt.Sprintf("runner reported %d errors", len(result.Errors)))
	}
	if !allowStub && result.Evidence.Mode != "real-zone" {
		problems = append(problems, "production validation requires evidence.mode=real-zone")
	}
	if !result.Evidence.CredentialsUsed {
		problems = append(problems, "credentials_used is false")
	}
	if !allowStub && (!result.Evidence.TelemetryComplete || result.Evidence.TelemetrySource == "") {
		problems = append(problems, "complete normalized Cloudflare telemetry is required")
	}
	if !allowStub && (!result.Evidence.InjectionsRun || result.Evidence.InjectionSource == "") {
		problems = append(problems, "all fault injections must run against a credentialed control endpoint")
	}
	if len(result.Stages) != len(requiredStages) {
		problems = append(problems, fmt.Sprintf("got %d stages, want %d", len(result.Stages), len(requiredStages)))
	}
	for i, expectedReaders := range requiredStages {
		if i >= len(result.Stages) {
			break
		}
		validateStage(&problems, result.Stages[i], expectedReaders, allowStub)
	}
	if !allowStub {
		seen := make(map[string]bool, len(result.Injections))
		for _, injection := range result.Injections {
			if seen[injection.Name] {
				problems = append(problems, "duplicate injection "+injection.Name)
			}
			seen[injection.Name] = true
			if !injection.Requested || !injection.Observed || !injection.NoLeak || injection.Error != "" {
				problems = append(problems, fmt.Sprintf("injection %q was not observed without error or leak", injection.Name))
			}
		}
		for _, name := range []string{"cache-bypass", "eviction", "rule-change", "capability-expiry"} {
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
	if stage.Requests == 0 || stage.Successes == 0 || stage.Latency.Samples == 0 {
		*problems = append(*problems, prefix+" has no request/latency evidence")
	}
	if stage.Successes+stage.Errors != stage.Requests {
		*problems = append(*problems, prefix+" request accounting does not balance")
	}
	if stage.Latency.P50 < 0 || stage.Latency.P95 < stage.Latency.P50 || stage.Latency.P99 < stage.Latency.P95 || stage.Latency.Max < stage.Latency.P99 || math.IsNaN(stage.Latency.P99) {
		*problems = append(*problems, prefix+" latency percentiles are invalid")
	}
	if !stage.OrderedBytes.OK || stage.OrderedBytes.Responses == 0 || stage.OrderedBytes.Violations != 0 {
		*problems = append(*problems, prefix+" did not prove ordered byte equality")
	}
	if !stage.Isolation.OK || stage.Isolation.Attempts == 0 || stage.Isolation.LeakedBytes != 0 {
		*problems = append(*problems, prefix+" did not prove capability isolation")
	}
	if stage.Worker == nil {
		if !allowStub {
			*problems = append(*problems, prefix+" has no Worker request metric")
		}
	} else if *stage.Worker != 0 {
		*problems = append(*problems, fmt.Sprintf("%s Worker requests=%d, want 0", prefix, *stage.Worker))
	}
	if stage.CostPerMillion == nil || *stage.CostPerMillion < 0 || math.IsNaN(*stage.CostPerMillion) {
		if !allowStub {
			*problems = append(*problems, prefix+" has no measured reader-hour cost")
		}
	}
	if allowStub {
		return
	}
	if stage.Cache.EdgeMisses == nil || stage.Cache.UpperTierMisses == nil || stage.Cache.OriginFetches == nil || stage.Cache.TotalBytes == nil || stage.Cache.CacheHitBytes == nil || stage.Cache.ByteHitRatio == nil {
		*problems = append(*problems, prefix+" has incomplete cache/origin telemetry")
		return
	}
	if *stage.Cache.CacheHitBytes > *stage.Cache.TotalBytes || *stage.Cache.ByteHitRatio < 0 || *stage.Cache.ByteHitRatio > 1 || math.IsNaN(*stage.Cache.ByteHitRatio) {
		*problems = append(*problems, prefix+" cache byte-hit ratio is invalid")
	}
	wantRatio := 0.0
	if *stage.Cache.TotalBytes > 0 {
		wantRatio = float64(*stage.Cache.CacheHitBytes) / float64(*stage.Cache.TotalBytes)
	}
	if math.Abs(*stage.Cache.ByteHitRatio-wantRatio) > 0.000001 {
		*problems = append(*problems, prefix+" cache byte-hit ratio does not match byte counters")
	}
}

func joinProblems(problems []string) string {
	result := "invalid X-5 result:"
	for _, problem := range problems {
		result += " " + problem + ";"
	}
	return result
}
