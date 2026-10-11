package benchmark

import (
	"encoding/json"
	"fmt"
	"io"
	"math"
	"os"
	"regexp"
)

var namePattern = regexp.MustCompile(`^[a-z][a-z0-9-]{0,62}$`)
var Operations = map[string]bool{"doc.create": true, "doc.get": true, "doc.update": true, "doc.delete": true, "query.simple": true, "query.indexed": true, "txn.multi": true}

type Mix struct {
	Op           string `json:"op"`
	Weight       int    `json:"weight"`
	DocSizeBytes int    `json:"doc_size_bytes,omitempty"`
}
type Dataset struct {
	Documents    int `json:"documents"`
	DocSizeBytes int `json:"doc_size_bytes"`
}
type Targets struct {
	P99MS float64 `json:"p99_ms"`
}
type Ramp struct {
	StepS       int   `json:"step_s"`
	Concurrency []int `json:"concurrency"`
}
type Workload struct {
	Name        string  `json:"name"`
	DurationS   int     `json:"duration_s"`
	WarmupS     int     `json:"warmup_s"`
	Concurrency int     `json:"concurrency"`
	Dataset     Dataset `json:"dataset"`
	Mix         []Mix   `json:"mix"`
	Targets     Targets `json:"targets"`
	Ramp        *Ramp   `json:"ramp,omitempty"`
}

func Load(path string) (Workload, error) {
	var w Workload
	f, err := os.Open(path)
	if err != nil {
		return w, err
	}
	defer f.Close()
	d := json.NewDecoder(io.LimitReader(f, 1<<20))
	d.DisallowUnknownFields()
	if err = d.Decode(&w); err != nil {
		return w, err
	}
	var extra any
	if d.Decode(&extra) != io.EOF {
		return w, fmt.Errorf("workload must be one JSON object")
	}
	return w, w.Validate()
}
func (w Workload) Validate() error {
	if !namePattern.MatchString(w.Name) || w.DurationS < 1 || w.DurationS > 600 || w.WarmupS < 0 || w.WarmupS > 60 || w.Concurrency < 1 || w.Concurrency > 256 || w.Dataset.Documents < 1 || w.Dataset.Documents > 10000 || w.Dataset.DocSizeBytes < 32 || w.Dataset.DocSizeBytes > 1<<20 || w.Targets.P99MS < 1 || w.Targets.P99MS > 30000 || math.IsNaN(w.Targets.P99MS) || math.IsInf(w.Targets.P99MS, 0) {
		return fmt.Errorf("workload name, duration, concurrency, dataset or p99 target outside protocol bounds")
	}
	total := 0
	seen := map[string]bool{}
	for _, m := range w.Mix {
		if !Operations[m.Op] || seen[m.Op] || m.Weight < 1 || m.Weight > 100 || m.DocSizeBytes < 0 || m.DocSizeBytes > 1<<20 || (m.DocSizeBytes > 0 && m.DocSizeBytes < 32) {
			return fmt.Errorf("invalid mix entry %q", m.Op)
		}
		seen[m.Op] = true
		total += m.Weight
	}
	if total != 100 {
		return fmt.Errorf("mix weights must sum to 100")
	}
	if w.Name == "ramp" && w.Ramp == nil {
		return fmt.Errorf("ramp requires steps")
	}
	if w.Ramp != nil {
		if w.Ramp.StepS < 5 || w.Ramp.StepS > 600 || len(w.Ramp.Concurrency) > 7 || len(w.Ramp.Concurrency) < 2 || w.Ramp.StepS*len(w.Ramp.Concurrency) != w.DurationS || w.Ramp.Concurrency[0] != w.Concurrency {
			return fmt.Errorf("invalid ramp duration or initial concurrency")
		}
		prev := 0
		for _, c := range w.Ramp.Concurrency {
			if c <= prev || c > 256 {
				return fmt.Errorf("ramp concurrency must increase within 1..256")
			}
			prev = c
		}
	}
	return nil
}
func (w Workload) Pick(index int) Mix {
	slot := (index % 100 * 37) % 100
	for _, m := range w.Mix {
		if slot < m.Weight {
			return m
		}
		slot -= m.Weight
	}
	panic("unvalidated workload")
}
