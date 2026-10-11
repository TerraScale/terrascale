package apibench

import (
	"fmt"
	"math"
	"slices"
)

var writeScenarios = []string{"doc.insert", "doc.update", "doc.delete", "txn.batch", "mixed.80-20"}

type Sweep struct {
	Scenario      string
	Transport     string
	PayloadBytes  int
	Concurrency   []int
	OpenFractions []float64
}

// Plan lists the sweeps for one run. A non-empty payloads filters the sizes.
// "changing" drops concurrency 512 and 64 KB writes, which touch gru nodes.
func Plan(s Spec, payloads []int, clusterState string) ([]Sweep, error) {
	changing := clusterState == "changing"
	if !changing && clusterState != "stable" {
		return nil, fmt.Errorf("cluster state %q: want stable or changing", clusterState)
	}
	var plan []Sweep
	for _, sc := range s.Scenarios {
		var conc []int
		for _, c := range s.ConcurrencyFor(sc) {
			if !changing || c != 512 {
				conc = append(conc, c)
			}
		}
		if len(conc) == 0 {
			return nil, fmt.Errorf("scenario %s has no concurrency left in cluster state %s", sc.Name, clusterState)
		}
		write := slices.Contains(writeScenarios, sc.Name)
		for _, p := range s.PayloadsFor(sc) {
			if len(payloads) > 0 && !slices.Contains(payloads, p) {
				continue
			}
			if changing && write && p == 65536 {
				continue
			}
			transports := []string{"sdk"}
			if slices.Contains(s.RawBaseline.Scenarios, sc.Name) && slices.Contains(s.RawBaseline.PayloadBytes, p) {
				transports = append(transports, s.RawBaseline.Transports...)
			}
			for _, t := range transports {
				sw := Sweep{Scenario: sc.Name, Transport: t, PayloadBytes: p, Concurrency: conc}
				if t == "sdk" && slices.Contains(s.OpenLoop.Scenarios, sc.Name) {
					sw.OpenFractions = s.OpenLoop.Fractions
				}
				plan = append(plan, sw)
			}
		}
	}
	return plan, nil
}

// RunSweep runs closed-loop steps upwards until one breaches the thresholds,
// then open-loop steps at each fraction of the best passing req/s. It returns
// the results so far with any exec error.
func RunSweep(sw Sweep, s Spec, exec func(Step) (Result, error)) ([]Result, error) {
	var out []Result
	var base, bestRPS float64
	top := 0
	for i, c := range sw.Concurrency {
		st := sw.step(s, base)
		st.Concurrency = c
		r, err := exec(st)
		if err != nil {
			return out, err
		}
		judge(&r, s, base)
		out = append(out, r)
		if r.Stopped != "" {
			break
		}
		if i == 0 {
			base = r.Latency.P99
		}
		bestRPS = max(bestRPS, r.RPS)
		top = c
	}
	if top == 0 {
		return out, nil
	}
	for _, f := range sw.OpenFractions {
		st := sw.step(s, base)
		st.Mode = "open"
		st.Concurrency = top
		st.Rate = max(1, int(math.Round(f*bestRPS)))
		r, err := exec(st)
		if err != nil {
			return out, err
		}
		judge(&r, s, base)
		out = append(out, r)
		if r.Stopped != "" {
			break
		}
	}
	return out, nil
}

func (sw Sweep) step(s Spec, base float64) Step {
	return Step{
		Type:           "step",
		Scenario:       sw.Scenario,
		Transport:      sw.Transport,
		PayloadBytes:   sw.PayloadBytes,
		Mode:           "closed",
		WarmupS:        s.WarmupS,
		StepS:          s.StepS,
		AbortErrorRate: s.Safety.MaxErrorRate,
		AbortP99US:     int64(base * s.Safety.MaxP99Ratio),
	}
}

// judge marks a step the client did not abort but that breaches a threshold.
func judge(r *Result, s Spec, base float64) {
	switch {
	case r.Stopped != "":
	case r.ErrorRate > s.Safety.MaxErrorRate:
		r.Stopped = "error rate"
	case base > 0 && r.Latency.P99 > base*s.Safety.MaxP99Ratio:
		r.Stopped = "p99"
	}
}
