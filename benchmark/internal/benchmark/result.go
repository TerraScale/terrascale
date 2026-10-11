package benchmark

import (
	"fmt"
	hdr "github.com/HdrHistogram/hdrhistogram-go"
	"time"
)

const HistogramMaxUS int64 = 900_000_000

func histogram() *hdr.Histogram { return hdr.New(1, HistogramMaxUS, 3) }

type Stats struct {
	Count     int64         `json:"count"`
	Errors    int64         `json:"errors"`
	P50US     int64         `json:"p50_us"`
	P95US     int64         `json:"p95_us"`
	P99US     int64         `json:"p99_us"`
	MaxUS     int64         `json:"max_us"`
	Histogram *hdr.Snapshot `json:"histogram,omitempty"`
}
type Window struct {
	TS         time.Time `json:"ts"`
	Client     string    `json:"client"`
	SDK        string    `json:"sdk"`
	SDKVersion string    `json:"sdk_version"`
	Region     string    `json:"region"`
	Op         string    `json:"op"`
	Stats
	Step        int     `json:"step"`
	Concurrency int     `json:"concurrency"`
	ElapsedS    float64 `json:"elapsed_s"`
}
type Step struct {
	Concurrency int              `json:"concurrency"`
	Start       time.Time        `json:"start"`
	End         time.Time        `json:"end"`
	ElapsedS    float64          `json:"elapsed_s"`
	OpsPerS     float64          `json:"ops_per_s"`
	ErrorRate   float64          `json:"error_rate"`
	PerOp       map[string]Stats `json:"per_op"`
	Completed   bool             `json:"completed"`
}
type Summary struct {
	SDK            string           `json:"sdk,omitempty"`
	SDKVersion     string           `json:"sdk_version,omitempty"`
	Type           string           `json:"type"`
	Client         string           `json:"client"`
	Region         string           `json:"region"`
	Workload       string           `json:"workload"`
	WorkloadConfig Workload         `json:"workload_config"`
	RunID          string           `json:"run_id"`
	Start          time.Time        `json:"start"`
	End            time.Time        `json:"end"`
	ElapsedS       float64          `json:"elapsed_s"`
	OpsPerS        float64          `json:"ops_per_s"`
	ErrorRate      float64          `json:"error_rate"`
	PerOp          map[string]Stats `json:"per_op"`
	Steps          []Step           `json:"steps"`
	Cleanup        bool             `json:"cleanup"`
	Stopped        string           `json:"stopped,omitempty"`
}
type accumulator struct {
	count, errors int64
	h             *hdr.Histogram
}

func newAccumulator() *accumulator { return &accumulator{h: histogram()} }
func (a *accumulator) record(us int64, err error) {
	a.count++
	if err != nil {
		a.errors++
		return
	}
	if e := a.h.RecordValue(max(1, min(us, HistogramMaxUS))); e != nil {
		panic(e)
	}
}
func (a *accumulator) stats(export bool) Stats {
	s := Stats{Count: a.count, Errors: a.errors, P50US: a.h.ValueAtQuantile(50), P95US: a.h.ValueAtQuantile(95), P99US: a.h.ValueAtQuantile(99), MaxUS: a.h.Max()}
	if export {
		s.Histogram = a.h.Export()
	}
	return s
}
func (s Stats) Valid() error {
	if s.Count < 0 || s.Count > 10_000_000 || s.Errors < 0 || s.Errors > s.Count || s.P50US < 0 || s.P50US > s.P95US || s.P95US > s.P99US || s.P99US > s.MaxUS || s.MaxUS > HistogramMaxUS {
		return fmt.Errorf("invalid count or latency")
	}
	if s.Histogram == nil || s.Histogram.LowestTrackableValue != 1 || s.Histogram.HighestTrackableValue != HistogramMaxUS || s.Histogram.SignificantFigures != 3 || len(s.Histogram.Counts) != len(histogram().Export().Counts) {
		return fmt.Errorf("missing or incompatible HDR histogram")
	}
	for _, n := range s.Histogram.Counts {
		if n < 0 || n > s.Count {
			return fmt.Errorf("invalid histogram bucket")
		}
	}
	h := hdr.Import(s.Histogram)
	if h.TotalCount() != s.Count-s.Errors || h.ValueAtQuantile(50) != s.P50US || h.ValueAtQuantile(95) != s.P95US || h.ValueAtQuantile(99) != s.P99US || h.Max() != s.MaxUS {
		return fmt.Errorf("histogram and stats disagree")
	}
	return nil
}
func importHistogram(s Stats) *hdr.Histogram { return hdr.Import(s.Histogram) }
