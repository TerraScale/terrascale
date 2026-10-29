package apibench

import (
	"slices"
	"strings"
	"testing"
)

func loadWorkload(t *testing.T) Spec {
	t.Helper()
	s, err := LoadSpec(workload)
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestPlanCounts(t *testing.T) {
	t.Parallel()
	s := loadWorkload(t)
	stable, err := Plan(s, nil, "stable")
	if err != nil || len(stable) != 24 {
		t.Fatalf("stable: %d sweeps, err %v; want 24", len(stable), err)
	}
	changing, err := Plan(s, nil, "changing")
	if err != nil || len(changing) != 19 {
		t.Fatalf("changing: %d sweeps, err %v; want 19", len(changing), err)
	}
	for _, sw := range changing {
		if slices.Contains(sw.Concurrency, 512) || sw.PayloadBytes == 65536 && slices.Contains(writeScenarios, sw.Scenario) {
			t.Fatalf("changing kept %+v", sw)
		}
	}
	only4k, err := Plan(s, []int{4096}, "stable")
	if err != nil || len(only4k) != 10 {
		t.Fatalf("payload 4096: %d sweeps, err %v; want 10", len(only4k), err)
	}
	if _, err := Plan(s, nil, "bogus"); err == nil {
		t.Fatal("accepted bogus cluster state")
	}
}

func TestPlanErrorsWhenNoConcurrencyLeft(t *testing.T) {
	t.Parallel()
	s := loadWorkload(t)
	s.Scenarios = []Scenario{{Name: "doc.get", Concurrency: []int{512}}}
	if _, err := Plan(s, nil, "changing"); err == nil || !strings.Contains(err.Error(), "doc.get") || !strings.Contains(err.Error(), "changing") {
		t.Fatalf("got err %v; want one naming doc.get and changing", err)
	}
}

func TestPlanOpenLoopOnSDKOnly(t *testing.T) {
	t.Parallel()
	plan, err := Plan(loadWorkload(t), []int{4096}, "stable")
	if err != nil {
		t.Fatal(err)
	}
	for _, sw := range plan {
		if sw.OpenFractions != nil && (sw.Transport != "sdk" || sw.Scenario != "doc.get" && sw.Scenario != "mixed.80-20") {
			t.Fatalf("open-loop fractions on %+v", sw)
		}
	}
}

func TestRunSweepStopsOnErrorRate(t *testing.T) {
	t.Parallel()
	sw := Sweep{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 4096, Concurrency: []int{1, 8, 32}}
	var steps []Step
	rs, err := RunSweep(sw, loadWorkload(t), func(st Step) (Result, error) {
		steps = append(steps, st)
		r := Result{RPS: float64(st.Concurrency) * 100, Latency: Latency{P99: 1000}}
		if st.Concurrency == 8 {
			r.ErrorRate = 0.5
		}
		return r, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(rs) != 2 || rs[1].Stopped != "error rate" || len(steps) != 2 {
		t.Fatalf("results %+v", rs)
	}
	if steps[0].AbortP99US != 0 || steps[1].AbortP99US != 10000 || steps[1].AbortErrorRate != 0.01 {
		t.Fatalf("abort thresholds %+v", steps)
	}
}

func TestRunSweepStopsOnP99Ratio(t *testing.T) {
	t.Parallel()
	sw := Sweep{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 4096, Concurrency: []int{1, 8, 32}}
	rs, err := RunSweep(sw, loadWorkload(t), func(st Step) (Result, error) {
		p99 := 1000.0
		if st.Concurrency == 8 {
			p99 = 20000
		}
		return Result{RPS: 100, Latency: Latency{P99: p99}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(rs) != 2 || rs[1].Stopped != "p99" {
		t.Fatalf("results %+v", rs)
	}
}

func TestRunSweepDerivesOpenLoopSteps(t *testing.T) {
	t.Parallel()
	sw := Sweep{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 4096, Concurrency: []int{1, 8, 32}, OpenFractions: []float64{0.5, 0.9}}
	var steps []Step
	rs, err := RunSweep(sw, loadWorkload(t), func(st Step) (Result, error) {
		steps = append(steps, st)
		r := Result{RPS: float64(st.Concurrency) * 100, Latency: Latency{P99: 1000}}
		if st.Mode == "open" {
			r.RPS = float64(st.Rate)
		}
		if st.Concurrency == 32 && st.Mode == "closed" {
			r.ErrorRate = 0.5
		}
		return r, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(rs) != 5 || len(steps) != 5 {
		t.Fatalf("got %d results, want 3 closed + 2 open", len(rs))
	}
	if o := steps[3]; o.Mode != "open" || o.Rate != 400 || o.Concurrency != 8 {
		t.Fatalf("first open step %+v; want rate 400 (0.5 x 800 rps) at concurrency 8", o)
	}
	if o := steps[4]; o.Rate != 720 {
		t.Fatalf("second open step rate %d; want 720", o.Rate)
	}
}

func TestRunSweepSkipsOpenLoopWhenNothingPassed(t *testing.T) {
	t.Parallel()
	sw := Sweep{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 4096, Concurrency: []int{1, 8}, OpenFractions: []float64{0.5}}
	rs, err := RunSweep(sw, loadWorkload(t), func(st Step) (Result, error) {
		return Result{ErrorRate: 1}, nil
	})
	if err != nil || len(rs) != 1 || rs[0].Stopped != "error rate" {
		t.Fatalf("results %+v, err %v", rs, err)
	}
}
