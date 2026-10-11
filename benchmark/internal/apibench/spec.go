package apibench

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"slices"
)

type Scenario struct {
	Name         string `json:"name"`
	PayloadBytes []int  `json:"payload_bytes,omitempty"`
	Concurrency  []int  `json:"concurrency,omitempty"`
}

type Safety struct {
	MaxErrorRate float64 `json:"max_error_rate"`
	MaxP99Ratio  float64 `json:"max_p99_ratio"`
}

type OpenLoop struct {
	Scenarios []string  `json:"scenarios"`
	Fractions []float64 `json:"fractions"`
}

type RawBaseline struct {
	Scenarios    []string `json:"scenarios"`
	PayloadBytes []int    `json:"payload_bytes"`
	Transports   []string `json:"transports"`
}

type Spec struct {
	Name          string      `json:"name"`
	Endpoint      string      `json:"endpoint"`
	AuthIssuer    string      `json:"auth_issuer"`
	StepS         int         `json:"step_s"`
	WarmupS       int         `json:"warmup_s"`
	SeedDocuments int         `json:"seed_documents"`
	PayloadBytes  []int       `json:"payload_bytes"`
	Concurrency   []int       `json:"concurrency"`
	Safety        Safety      `json:"safety"`
	Scenarios     []Scenario  `json:"scenarios"`
	OpenLoop      OpenLoop    `json:"open_loop"`
	RawBaseline   RawBaseline `json:"raw_baseline"`
}

var knownScenarios = []string{"auth.token", "doc.get", "doc.insert", "doc.update", "doc.delete", "query.small", "txn.batch", "mixed.80-20"}

// LoadSpec reads and validates a benchmark spec; unknown fields are an error.
func LoadSpec(path string) (Spec, error) {
	var s Spec
	f, err := os.Open(path)
	if err != nil {
		return s, err
	}
	defer f.Close()
	dec := json.NewDecoder(f)
	dec.DisallowUnknownFields()
	if err := dec.Decode(&s); err != nil {
		return s, fmt.Errorf("%s: %w", path, err)
	}
	if _, err := dec.Token(); err != io.EOF {
		return s, fmt.Errorf("%s: trailing data after spec", path)
	}
	if err := s.validate(); err != nil {
		return s, fmt.Errorf("%s: %w", path, err)
	}
	return s, nil
}

// PayloadsFor returns the scenario's payload sizes, or the spec default.
func (s Spec) PayloadsFor(sc Scenario) []int {
	if len(sc.PayloadBytes) > 0 {
		return sc.PayloadBytes
	}
	return s.PayloadBytes
}

// ConcurrencyFor returns the scenario's concurrency steps, or the spec default.
func (s Spec) ConcurrencyFor(sc Scenario) []int {
	if len(sc.Concurrency) > 0 {
		return sc.Concurrency
	}
	return s.Concurrency
}

func (s Spec) validate() error {
	if !httpsURL(s.Endpoint) || !httpsURL(s.AuthIssuer) {
		return errors.New("endpoint and auth_issuer must be https URLs")
	}
	if s.StepS < 1 || s.WarmupS < 0 || s.StepS+s.WarmupS > 60 {
		return errors.New("want step_s >= 1, warmup_s >= 0 and step_s + warmup_s <= 60")
	}
	if s.SeedDocuments < 1 || s.SeedDocuments > 10000 {
		return errors.New("seed_documents must be 1..10000")
	}
	if s.Safety.MaxErrorRate <= 0 || s.Safety.MaxErrorRate >= 1 || s.Safety.MaxP99Ratio <= 1 {
		return errors.New("want 0 < max_error_rate < 1 and max_p99_ratio > 1")
	}
	if err := checkAxes("spec", s.PayloadBytes, s.Concurrency); err != nil {
		return err
	}
	if len(s.Scenarios) == 0 {
		return errors.New("no scenarios")
	}
	seen := map[string]bool{}
	for _, sc := range s.Scenarios {
		if !slices.Contains(knownScenarios, sc.Name) {
			return fmt.Errorf("unknown scenario %q", sc.Name)
		}
		if seen[sc.Name] {
			return fmt.Errorf("duplicate scenario %q", sc.Name)
		}
		seen[sc.Name] = true
		if err := checkAxes(sc.Name, s.PayloadsFor(sc), s.ConcurrencyFor(sc)); err != nil {
			return err
		}
	}
	for _, names := range [][]string{s.OpenLoop.Scenarios, s.RawBaseline.Scenarios} {
		for _, n := range names {
			if !seen[n] {
				return fmt.Errorf("%q is not in scenarios", n)
			}
		}
	}
	for _, f := range s.OpenLoop.Fractions {
		if f <= 0 || f > 1 {
			return fmt.Errorf("open_loop fraction %v not in (0,1]", f)
		}
	}
	for _, p := range s.RawBaseline.PayloadBytes {
		if !validPayload("raw_baseline", p) {
			return fmt.Errorf("raw_baseline payload %d not allowed", p)
		}
	}
	for _, t := range s.RawBaseline.Transports {
		if t != "raw-h2" && t != "raw-h1" {
			return fmt.Errorf("unknown raw transport %q", t)
		}
	}
	return nil
}

func checkAxes(name string, payloads, concurrency []int) error {
	if len(payloads) == 0 || len(concurrency) == 0 {
		return fmt.Errorf("%s: payload_bytes and concurrency must be non-empty", name)
	}
	for _, p := range payloads {
		if !validPayload(name, p) {
			return fmt.Errorf("%s: payload %d not allowed", name, p)
		}
	}
	for i, c := range concurrency {
		if c < 1 || c > 512 || i > 0 && c <= concurrency[i-1] {
			return fmt.Errorf("%s: concurrency must be strictly increasing within 1..512", name)
		}
	}
	return nil
}

// validPayload allows 0 only for auth.token, which sends no document.
func validPayload(name string, p int) bool {
	if name == "auth.token" {
		return p == 0
	}
	return p >= 256 && p <= 1<<20
}

func httpsURL(raw string) bool {
	u, err := url.Parse(raw)
	return err == nil && u.Scheme == "https" && u.Host != ""
}
