package benchmark

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/oklog/ulid/v2"
	"io"
	"math"
	"os"
	"reflect"
	"strings"
	"time"
)

type Output struct {
	Summary Summary
	Windows []Window
}

func Read(path string, w Workload, region string) (Output, error) {
	var out Output
	f, err := os.Open(path)
	if err != nil {
		return out, err
	}
	defer f.Close()
	return read(f, w, region)
}
func read(r io.Reader, w Workload, region string) (Output, error) {
	var out Output
	scan := bufio.NewScanner(io.LimitReader(r, 128<<20))
	scan.Buffer(make([]byte, 4096), 8<<20)
	finished := false
	counts, errs := map[string]int64{}, map[string]int64{}
	seen := map[string]bool{}
	ops := map[string]bool{}
	for _, m := range w.Mix {
		ops[m.Op] = true
	}
	for scan.Scan() {
		if finished {
			return out, errors.New("data after summary")
		}
		line := scan.Bytes()
		var kind struct {
			Type string `json:"type"`
		}
		if err := json.Unmarshal(line, &kind); err != nil {
			return out, err
		}
		if kind.Type == "summary" {
			if err := json.Unmarshal(line, &out.Summary); err != nil {
				return out, err
			}
			finished = true
			continue
		}
		if kind.Type != "" {
			return out, errors.New("unknown JSONL record type")
		}
		var row Window
		if err := json.Unmarshal(line, &row); err != nil {
			return out, err
		}
		key := fmt.Sprintf("%d/%s", row.TS.UnixNano(), row.Op)
		if row.Region != region || row.Client == "" || row.SDK == "" || row.SDKVersion == "" || !ops[row.Op] || row.TS.IsZero() || row.Count < 0 || row.Errors < 0 || row.Errors > row.Count || row.Count > 100000 || row.ElapsedS <= 0 || row.ElapsedS > 6 || math.IsNaN(row.ElapsedS) || row.Step < 0 || row.Concurrency < 1 || row.Concurrency > 256 || seen[key] || row.P50US < 0 || row.P50US > row.P95US || row.P95US > row.P99US || row.P99US > row.MaxUS || row.MaxUS > HistogramMaxUS {
			return out, errors.New("invalid, duplicate or mismatched JSONL window")
		}
		seen[key] = true
		counts[row.Op] += row.Count
		errs[row.Op] += row.Errors
		out.Windows = append(out.Windows, row)
	}
	if err := scan.Err(); err != nil {
		return out, err
	}
	if !finished || len(out.Windows) == 0 {
		return out, errors.New("missing windows or final summary")
	}
	s := out.Summary
	s.SDK = out.Windows[0].SDK
	s.SDKVersion = out.Windows[0].SDKVersion
	out.Summary = s
	if !reflect.DeepEqual(s.WorkloadConfig, w) {
		return out, errors.New("summary workload_config does not match requested workload")
	}
	if s.Region != region || s.Workload != w.Name || s.Client == "" || s.ElapsedS <= 0 || s.ElapsedS > 900 || !s.Start.Before(s.End) || len(s.Steps) == 0 || len(s.Steps) > 7 || len(s.PerOp) != len(ops) {
		return out, errors.New("invalid summary identity, timing or operations")
	}
	if _, err := ulid.ParseStrict(s.RunID); err != nil {
		return out, errors.New("run_id must be a plain ULID")
	}
	for op, v := range s.PerOp {
		if !ops[op] || v.Count > 10_000_000 || v.Valid() != nil || v.Count != counts[op] || v.Errors != errs[op] {
			return out, errors.New("summary histogram/counts disagree with windows")
		}
	}
	phaseCounts := make([]map[string]Stats, len(s.Steps))
	for _, row := range out.Windows {
		if row.Client != s.Client || row.SDK != s.SDK || row.SDKVersion != s.SDKVersion || row.TS.Before(s.Start.Add(-time.Millisecond)) || row.TS.After(s.End) || row.Step >= len(s.Steps) || row.Concurrency != s.Steps[row.Step].Concurrency {
			return out, errors.New("window outside run or mismatched step")
		}
		step := s.Steps[row.Step]
		end := row.TS.Add(time.Duration(row.ElapsedS * float64(time.Second)))
		if row.TS.Before(step.Start.Add(-time.Millisecond)) || end.After(step.End.Add(time.Millisecond)) {
			return out, errors.New("window outside assigned phase interval")
		}
		if phaseCounts[row.Step] == nil {
			phaseCounts[row.Step] = map[string]Stats{}
		}
		v := phaseCounts[row.Step][row.Op]
		v.Count += row.Count
		v.Errors += row.Errors
		phaseCounts[row.Step][row.Op] = v
	}
	if w.Ramp != nil {
		if s.Stopped == "" && len(s.Steps) != len(w.Ramp.Concurrency) {
			return out, errors.New("unfinished ramp without a stop reason")
		}
		for i, step := range s.Steps {
			if !step.Completed && (s.Stopped == "" || i < len(s.Steps)-1) {
				return out, errors.New("incomplete ramp phase without a terminal stop")
			}
		}
		if s.Stopped == "ramp safety threshold exceeded" && s.Steps[len(s.Steps)-1].Completed {
			return out, errors.New("ramp safety stop requires an incomplete terminal phase")
		}
	}
	previous := s.Start
	for i, step := range s.Steps {
		expectedC, expectedS := w.Concurrency, w.DurationS
		if w.Ramp != nil {
			if i >= len(w.Ramp.Concurrency) {
				return out, errors.New("extra ramp step")
			}
			expectedC = w.Ramp.Concurrency[i]
			expectedS = w.Ramp.StepS
		} else if len(s.Steps) != 1 {
			return out, errors.New("extra fixed workload step")
		}
		if step.Start.Before(previous) || !step.Start.Before(step.End) || step.Concurrency != expectedC || !closeNumber(step.ElapsedS, step.End.Sub(step.Start).Seconds()) || step.ElapsedS > float64(expectedS)+5.1 || (step.Completed && step.ElapsedS < float64(expectedS)) || len(step.PerOp) != len(ops) {
			return out, errors.New("invalid phase interval, concurrency or completion")
		}
		previous = step.End
		var count, failures int64
		for op, v := range step.PerOp {
			windows := phaseCounts[i][op]
			if !ops[op] || v.Valid() != nil || v.Count != windows.Count || v.Errors != windows.Errors {
				return out, errors.New("invalid phase operations")
			}
			count += v.Count
			failures += v.Errors
		}
		rate := float64(0)
		if count > 0 {
			rate = float64(failures) / float64(count)
		}
		if !closeNumber(step.OpsPerS, float64(count-failures)/step.ElapsedS) || !closeNumber(step.ErrorRate, rate) {
			return out, errors.New("invalid phase throughput or error rate")
		}
	}
	// Rebuild from step histograms, never average percentiles or client rates.
	rebuilt, err := Summarize(s.RunID, s.Region, w, s.Steps, s.Cleanup, s.Stopped)
	if err != nil {
		return out, err
	}
	if !rebuilt.Start.Equal(s.Start) || !rebuilt.End.Equal(s.End) || !closeNumber(rebuilt.ElapsedS, s.ElapsedS) || !closeNumber(rebuilt.OpsPerS, s.OpsPerS) || !closeNumber(rebuilt.ErrorRate, s.ErrorRate) {
		return out, errors.New("summary throughput or error rate disagrees with steps")
	}
	for op, v := range rebuilt.PerOp {
		original := s.PerOp[op]
		if v.Count != original.Count || v.Errors != original.Errors || !equalHistogram(v.Histogram.Counts, original.Histogram.Counts) {
			return out, errors.New("step and summary histograms disagree")
		}
	}
	return out, nil
}
func closeNumber(a, b float64) bool {
	return !math.IsNaN(b) && !math.IsInf(b, 0) && math.Abs(a-b) <= max(1e-6, math.Abs(a)*1e-6)
}
func equalHistogram(a, b []int64) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

type Metrics struct {
	Status string    `json:"status"`
	Reason string    `json:"reason"`
	Start  time.Time `json:"start"`
	End    time.Time `json:"end"`
}
type Report struct {
	ID              string            `json:"id"`
	Region          string            `json:"region"`
	Workload        Workload          `json:"workload"`
	Inputs          []string          `json:"inputs"`
	Runs            []Summary         `json:"runs"`
	PerOp           map[string]Stats  `json:"per_op"`
	Start           time.Time         `json:"start"`
	End             time.Time         `json:"end"`
	OpsPerS         float64           `json:"ops_per_s"`
	ErrorRate       float64           `json:"error_rate"`
	CapacityOpsPerS float64           `json:"capacity_ops_per_s"`
	Verdict         string            `json:"verdict"`
	Metrics         Metrics           `json:"cluster_metrics"`
	Stored          bool              `json:"stored"`
	ClientExecution map[string]string `json:"client_execution,omitempty"`
}

func Aggregate(inputs []string, w Workload, region string) (Report, []Window, error) {
	r := Report{ID: ulid.Make().String(), Region: region, Workload: w, Inputs: inputs, PerOp: map[string]Stats{}, Verdict: "PASS"}
	acc := map[string]*accumulator{}
	var rows []Window
	ids := map[string]bool{}
	for _, path := range inputs {
		o, err := Read(path, w, region)
		if err != nil {
			return r, nil, fmt.Errorf("%s: %w", path, err)
		}
		s := o.Summary
		if ids[s.RunID] {
			return r, nil, errors.New("duplicate run_id")
		}
		ids[s.RunID] = true
		r.Runs = append(r.Runs, s)
		rows = append(rows, o.Windows...)
		if r.Start.IsZero() || s.Start.Before(r.Start) {
			r.Start = s.Start
		}
		if s.End.After(r.End) {
			r.End = s.End
		}
		if !s.Cleanup || s.Stopped != "" {
			r.Verdict = "FAIL"
		}
		for _, step := range s.Steps {
			if !step.Completed {
				r.Verdict = "FAIL"
			}
		}
		for op, v := range s.PerOp {
			a := acc[op]
			if a == nil {
				a = newAccumulator()
				acc[op] = a
			}
			a.count += v.Count
			a.errors += v.Errors
			if a.h.Merge(importHistogram(v)) != 0 {
				return r, nil, errors.New("merge dropped samples")
			}
		}
	}
	if len(inputs) == 0 {
		return r, nil, errors.New("at least one input required")
	}
	var n, e int64
	for op, a := range acc {
		r.PerOp[op] = a.stats(true)
		n += a.count
		e += a.errors
	}
	r.OpsPerS = float64(n-e) / r.End.Sub(r.Start).Seconds()
	if n > 0 {
		r.ErrorRate = float64(e) / float64(n)
	}
	if n == 0 || r.ErrorRate > .01 {
		r.Verdict = "FAIL"
	}
	rampPass := true
	for _, s := range r.Runs {
		var capacity float64
		for _, step := range s.Steps {
			if step.ElapsedS <= 0 || step.ErrorRate > .01 || !step.Completed {
				continue
			}
			pass := true
			for _, stats := range step.PerOp {
				if stats.Count == stats.Errors || float64(stats.P99US) > w.Targets.P99MS*1000 {
					pass = false
				}
			}
			if pass {
				capacity = max(capacity, step.OpsPerS)
			}
		}
		r.CapacityOpsPerS = max(r.CapacityOpsPerS, capacity)
		if capacity == 0 || !s.Cleanup || (s.Stopped != "" && s.Stopped != "ramp safety threshold exceeded") {
			rampPass = false
		}
	}
	// Read requires a full completed ramp or an explicit terminal stop. A safety
	// stop preserves earlier passing steps; other interruptions still fail.
	if w.Ramp != nil {
		r.Verdict = "FAIL"
		if rampPass {
			r.Verdict = "PASS"
		}
	}
	if w.Ramp == nil {
		for _, v := range r.PerOp {
			if v.Count == v.Errors || float64(v.P99US) > w.Targets.P99MS*1000 {
				r.Verdict = "FAIL"
			}
		}
	}
	r.Metrics = Metrics{Status: "unavailable", Reason: "TS-761 slice 4 cluster-metrics API has not merged; no CPU or memory inferred", Start: r.Start, End: r.End}
	return r, rows, nil
}
func (r Report) Markdown() string {
	text := fmt.Sprintf("# %s / %s\n\nRun `%s`. UTC window: %s to %s.\n\n**Capacity verdict: %s** at p99 ≤ %.0f ms and errors ≤ 1%%.\n\n| Successful ops/s | Error rate | Sustained passing step (ops/s) | Cluster metrics | TerraBase stored |\n| ---: | ---: | ---: | --- | --- |\n| %.2f | %.3f%% | %.2f | unavailable | %t |\n\n| Operation | Attempts | Errors | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |\n", r.Region, r.Workload.Name, r.ID, r.Start.UTC().Format(time.RFC3339Nano), r.End.UTC().Format(time.RFC3339Nano), r.Verdict, r.Workload.Targets.P99MS, r.OpsPerS, r.ErrorRate*100, r.CapacityOpsPerS, r.Stored)
	for _, m := range r.Workload.Mix {
		s := r.PerOp[m.Op]
		text += fmt.Sprintf("| %s | %d | %d | %.3f | %.3f | %.3f | %.3f |\n", m.Op, s.Count, s.Errors, float64(s.P50US)/1000, float64(s.P95US)/1000, float64(s.P99US)/1000, float64(s.MaxUS)/1000)
	}
	text += "\nLatency includes delay from intended start; HDR histograms are merged before quantiles. Throughput excludes failed and dropped arrivals. These are observed passing rates, not a proven cluster saturation ceiling.\n\n" + r.Metrics.Reason + ".\n"
	text += "\n| Client / SDK | SDK version | Run ID | Cleanup | Stop reason | Process status |\n| --- | --- | --- | --- | --- | --- |\n"
	for _, s := range r.Runs {
		status := r.ClientExecution[s.RunID]
		if status == "" {
			status = "unavailable"
		}
		text += fmt.Sprintf("| %s / %s | %s | %s | %t | %s | %s |\n", cell(s.Client), cell(s.SDK), cell(s.SDKVersion), s.RunID, s.Cleanup, cell(s.Stopped), cell(status))
	}
	text += "\n| Client | Concurrency | Seconds | Ops/s | Errors | Completed |\n| --- | ---: | ---: | ---: | ---: | --- |\n"
	for _, s := range r.Runs {
		for _, step := range s.Steps {
			text += fmt.Sprintf("| %s | %d | %.3f | %.2f | %.3f%% | %t |\n", s.Client, step.Concurrency, step.ElapsedS, step.OpsPerS, step.ErrorRate*100, step.Completed)
		}
	}
	return text
}

func cell(s string) string {
	return strings.NewReplacer("|", "&#124;", "\n", " ", "\r", " ").Replace(s)
}
