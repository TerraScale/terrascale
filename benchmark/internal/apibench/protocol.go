package apibench

import (
	"encoding/json"
	"fmt"
	"io"
	"time"
)

type Startup struct {
	Type   string  `json:"type"`
	InitMS float64 `json:"init_ms"`
}

type Ready struct {
	Type       string  `json:"type"`
	Client     string  `json:"client"`
	SDK        string  `json:"sdk"`
	SDKVersion string  `json:"sdk_version"`
	Runtime    string  `json:"runtime"`
	Database   string  `json:"database"`
	SetupMS    float64 `json:"setup_ms"`
	AuthMS     float64 `json:"auth_ms"`
}

type Step struct {
	Type           string  `json:"type"`
	Scenario       string  `json:"scenario"`
	Transport      string  `json:"transport"`
	PayloadBytes   int     `json:"payload_bytes"`
	Mode           string  `json:"mode"`
	Concurrency    int     `json:"concurrency"`
	Rate           int     `json:"rate"`
	WarmupS        int     `json:"warmup_s"`
	StepS          int     `json:"step_s"`
	AbortErrorRate float64 `json:"abort_error_rate"`
	AbortP99US     int64   `json:"abort_p99_us"`
}

// Latency values are microseconds.
type Latency struct {
	P50  float64 `json:"p50"`
	P90  float64 `json:"p90"`
	P99  float64 `json:"p99"`
	P999 float64 `json:"p999"`
	Max  float64 `json:"max"`
	Mean float64 `json:"mean"`
}

type Result struct {
	Type         string           `json:"type"`
	Scenario     string           `json:"scenario"`
	Transport    string           `json:"transport"`
	PayloadBytes int              `json:"payload_bytes"`
	Mode         string           `json:"mode"`
	Concurrency  int              `json:"concurrency"`
	Rate         int              `json:"rate"`
	Start        time.Time        `json:"start"`
	DurationS    float64          `json:"duration_s"`
	Ops          int64            `json:"ops"`
	OK           int64            `json:"ok"`
	Errors       int64            `json:"errors"`
	ErrorRate    float64          `json:"error_rate"`
	RPS          float64          `json:"rps"`
	Status       map[string]int64 `json:"status"`
	Latency      Latency          `json:"latency_us"`
	BytesOut     int64            `json:"bytes_out"`
	BytesIn      int64            `json:"bytes_in"`
	CPUS         float64          `json:"cpu_s"`
	RSSMB        float64          `json:"rss_mb"`
	Stopped      string           `json:"stopped"`
}

type Done struct {
	Type         string `json:"type"`
	Cleanup      bool   `json:"cleanup"`
	CleanupError string `json:"cleanup_error"`
}

// Serve is the client side of the protocol: it announces ready, then answers
// one Result line per Step line until in reaches EOF. Serve returns at stdin EOF;
// the client then deletes its database and writes the final done line itself (docs/api-bench.md).
func Serve(in io.Reader, out io.Writer, ready Ready, run func(Step) Result) error {
	enc := json.NewEncoder(out)
	ready.Type = "ready"
	if err := enc.Encode(ready); err != nil {
		return err
	}
	dec := json.NewDecoder(in)
	dec.DisallowUnknownFields()
	for {
		var s Step
		err := dec.Decode(&s)
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}
		if err := checkStep(s); err != nil {
			return err
		}
		if err := enc.Encode(run(s)); err != nil {
			return err
		}
	}
}

// checkStep rejects a step the runner cannot run: arrivals divides by Rate, and a closed step needs a worker.
func checkStep(s Step) error {
	switch {
	case s.Mode != "closed" && s.Mode != "open":
		return fmt.Errorf("invalid step %q: mode %q, want closed or open", s.Scenario, s.Mode)
	case s.Concurrency < 1:
		return fmt.Errorf("invalid step %q: concurrency %d, want at least 1", s.Scenario, s.Concurrency)
	case s.Mode == "open" && s.Rate < 1:
		return fmt.Errorf("invalid step %q: rate %d, want at least 1 in open mode", s.Scenario, s.Rate)
	case s.StepS < 1:
		return fmt.Errorf("invalid step %q: step_s %d, want at least 1", s.Scenario, s.StepS)
	case s.WarmupS < 0:
		return fmt.Errorf("invalid step %q: warmup_s %d, want 0 or more", s.Scenario, s.WarmupS)
	}
	return nil
}
