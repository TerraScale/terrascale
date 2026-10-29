package benchmark

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/oklog/ulid/v2"
)

func rampFixture(t *testing.T) (Workload, Output) {
	t.Helper()
	w := fixture()
	w.Name, w.Concurrency, w.DurationS = "ramp", 1, 15
	w.Ramp = &Ramp{StepS: 5, Concurrency: []int{1, 2, 4}}
	w.Mix = []Mix{{Op: "doc.get", Weight: 70}, {Op: "doc.update", Weight: 30}}
	start := time.Date(2026, 10, 8, 0, 0, 0, 0, time.UTC)
	var o Output
	for i, concurrency := range w.Ramp.Concurrency {
		step := Step{Concurrency: concurrency, Start: start.Add(time.Duration(i*5) * time.Second), ElapsedS: 5, Completed: true, PerOp: map[string]Stats{}}
		step.End = step.Start.Add(5 * time.Second)
		totals := map[string]*accumulator{"doc.get": newAccumulator(), "doc.update": newAccumulator()}
		for second := 0; second < 5; second++ {
			for _, m := range w.Mix {
				a := newAccumulator()
				for n := 0; n < m.Weight/5; n++ {
					var err error
					if i == 0 && second == 0 && m.Op == "doc.update" && n == 0 {
						err = errors.New("failed update")
					}
					a.record(1000, err)
					totals[m.Op].record(1000, err)
				}
				o.Windows = append(o.Windows, Window{TS: step.Start.Add(time.Duration(second) * time.Second), Client: "go", SDK: "ts-go", SDKVersion: "test", Region: "gru", Op: m.Op, Stats: a.stats(false), Step: i, Concurrency: concurrency, ElapsedS: 1})
			}
		}
		var count, failures int64
		for op, a := range totals {
			step.PerOp[op] = a.stats(true)
			count += a.count
			failures += a.errors
		}
		step.OpsPerS = float64(count-failures) / step.ElapsedS
		step.ErrorRate = float64(failures) / float64(count)
		o.Summary.Steps = append(o.Summary.Steps, step)
	}
	s, err := Summarize(ulid.Make().String(), "gru", w, o.Summary.Steps, true, "")
	if err != nil {
		t.Fatal(err)
	}
	o.Summary = s
	return w, o
}

func encodeOutput(t *testing.T, o Output) []byte {
	t.Helper()
	var b bytes.Buffer
	enc := json.NewEncoder(&b)
	for _, row := range o.Windows {
		if err := enc.Encode(row); err != nil {
			t.Fatal(err)
		}
	}
	if err := enc.Encode(o.Summary); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}

func TestSummaryMustMatchFullWorkload(t *testing.T) {
	t.Parallel()
	w, o := rampFixture(t)
	raw := encodeOutput(t, o)
	if _, err := read(bytes.NewReader(raw), w, "gru"); err != nil {
		t.Fatal("matching measured configuration rejected", err)
	}
	for _, test := range []struct {
		name   string
		mutate func(*Workload)
	}{
		{"dataset documents", func(w *Workload) { w.Dataset.Documents++ }},
		{"dataset document size", func(w *Workload) { w.Dataset.DocSizeBytes++ }},
		{"mix weights", func(w *Workload) { w.Mix[0].Weight++; w.Mix[1].Weight-- }},
		{"mix document override", func(w *Workload) { w.Mix[1].DocSizeBytes = 64 }},
		{"mix order", func(w *Workload) { w.Mix[0], w.Mix[1] = w.Mix[1], w.Mix[0] }},
		{"warmup", func(w *Workload) { w.WarmupS++ }},
		{"target", func(w *Workload) { w.Targets.P99MS++ }},
		{"duration", func(w *Workload) { w.DurationS++ }},
		{"concurrency", func(w *Workload) { w.Concurrency++ }},
		{"ramp steps", func(w *Workload) { w.Ramp.Concurrency[2]++ }},
		{"ramp duration", func(w *Workload) { w.Ramp.StepS++ }},
		{"ramp absent", func(w *Workload) { w.Ramp = nil }},
	} {
		t.Run(test.name, func(t *testing.T) {
			lines := bytes.Split(bytes.TrimSpace(raw), []byte("\n"))
			var summary Summary
			if err := json.Unmarshal(lines[len(lines)-1], &summary); err != nil {
				t.Fatal(err)
			}
			test.mutate(&summary.WorkloadConfig)
			changed := o
			changed.Summary = summary
			if _, err := read(bytes.NewReader(encodeOutput(t, changed)), w, "gru"); err == nil || err.Error() != "summary workload_config does not match requested workload" {
				t.Fatal("accepted different measured configuration", err)
			}
		})
	}
	missing := bytes.Replace(raw, []byte(`"workload_config":`), []byte(`"absent_config":`), 1)
	if _, err := read(bytes.NewReader(missing), w, "gru"); err == nil {
		t.Fatal("accepted missing measured configuration")
	}
}

func TestWindowsMustAgreeWithAssignedPhase(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name   string
		mutate func(*Output)
	}{
		{"counts moved between phases", func(o *Output) { o.Windows[0].Count++; o.Windows[10].Count-- }},
		{"errors moved between phases", func(o *Output) { o.Windows[1].Errors--; o.Windows[11].Errors++ }},
		{"start outside phase", func(o *Output) { o.Windows[0].TS = o.Summary.Steps[1].Start.Add(100 * time.Millisecond) }},
		{"bucket crosses phase end", func(o *Output) { o.Windows[8].ElapsedS = 1.25 }},
	} {
		t.Run(test.name, func(t *testing.T) {
			w, o := rampFixture(t)
			if _, err := read(bytes.NewReader(encodeOutput(t, o)), w, "gru"); err != nil {
				t.Fatal("valid fixture rejected:", err)
			}
			test.mutate(&o)
			if _, err := read(bytes.NewReader(encodeOutput(t, o)), w, "gru"); err == nil {
				t.Fatal("accepted contradictory phase windows")
			}
		})
	}
}

func TestPhaseAllowsFinalPartialDrainBucket(t *testing.T) {
	t.Parallel()
	w, o := rampFixture(t)
	last := &o.Summary.Steps[2]
	last.End = last.End.Add(750 * time.Millisecond)
	last.ElapsedS = last.End.Sub(last.Start).Seconds()
	last.OpsPerS = 100 / last.ElapsedS
	o.Windows[28].Count--
	for _, m := range w.Mix {
		row := Window{TS: last.Start.Add(5 * time.Second), Client: "go", SDK: "ts-go", SDKVersion: "test", Region: "gru", Op: m.Op, Step: 2, Concurrency: 4, ElapsedS: .75}
		if m.Op == "doc.get" {
			a := newAccumulator()
			a.record(1000, nil)
			row.Stats = a.stats(false)
		}
		o.Windows = append(o.Windows, row)
	}
	s, err := Summarize(o.Summary.RunID, "gru", w, o.Summary.Steps, true, "")
	if err != nil {
		t.Fatal(err)
	}
	o.Summary = s
	if _, err := read(bytes.NewReader(encodeOutput(t, o)), w, "gru"); err != nil {
		t.Fatal("valid partial drain bucket rejected:", err)
	}
}

func TestRampRequiresCompletionOrSafetyStop(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name      string
		completed []bool
		stop      string
		valid     bool
		verdict   string
	}{
		{"full ramp", []bool{true, true, true}, "", true, "PASS"},
		{"missing phases", []bool{true}, "", false, ""},
		{"incomplete final phase", []bool{true, true, false}, "", false, ""},
		{"terminal safety stop", []bool{true, false}, "ramp safety threshold exceeded", true, "PASS"},
		{"terminal cancellation", []bool{true, false}, "context canceled", true, "FAIL"},
		{"incomplete earlier phase", []bool{false, true, false}, "ramp safety threshold exceeded", false, ""},
		{"safety stop without stopped phase", []bool{true, true}, "ramp safety threshold exceeded", false, ""},
	} {
		t.Run(test.name, func(t *testing.T) {
			w, o := rampFixture(t)
			o.Summary.Steps = o.Summary.Steps[:len(test.completed)]
			for i, completed := range test.completed {
				o.Summary.Steps[i].Completed = completed
			}
			o.Windows = o.Windows[:len(test.completed)*10]
			s, err := Summarize(o.Summary.RunID, "gru", w, o.Summary.Steps, true, test.stop)
			if err != nil {
				t.Fatal(err)
			}
			o.Summary = s
			path := filepath.Join(t.TempDir(), "ramp.jsonl")
			if err := os.WriteFile(path, encodeOutput(t, o), 0600); err != nil {
				t.Fatal(err)
			}
			r, _, err := Aggregate([]string{path}, w, "gru")
			if !test.valid {
				if err == nil {
					t.Fatal("accepted unfinished or contradictory ramp")
				}
				return
			}
			if err != nil || r.Verdict != test.verdict {
				t.Fatal(r.Verdict, err)
			}
		})
	}
}

func TestRampGuardUsesTotalErrorRate(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name    string
		errors  int
		latency int64
		unsafe  bool
	}{
		{"one failed update out of 100 arrivals", 1, 1000, false},
		{"two failed updates out of 100 arrivals", 2, 1000, true},
		{"update latency exceeds twice target", 0, 250000, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			bucket := map[string]*accumulator{"doc.get": newAccumulator(), "doc.update": newAccumulator()}
			for i := 0; i < 70; i++ {
				bucket["doc.get"].record(1000, nil)
			}
			for i := 0; i < 30; i++ {
				var err error
				if i < test.errors {
					err = errors.New("failed update")
				}
				bucket["doc.update"].record(test.latency, err)
			}
			if rampSafetyExceeded(bucket, 100) != test.unsafe {
				t.Fatal("guard disagrees with total error rate or per-op latency")
			}
		})
	}
	if rampSafetyExceeded(map[string]*accumulator{}, 100) {
		t.Fatal("empty bucket exceeded guard")
	}
}

func TestMultipleClientRamps(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name      string
		stop      string
		cleanup   bool
		verdict   string
		noPassing bool
	}{
		{"all clients complete", "", true, "PASS", false},
		{"one client safety stops", "ramp safety threshold exceeded", true, "PASS", false},
		{"one client cancels", "context canceled", true, "FAIL", false},
		{"one cleanup fails", "", false, "FAIL", false},
		{"one client has no passing phase", "ramp safety threshold exceeded", true, "FAIL", true},
	} {
		t.Run(test.name, func(t *testing.T) {
			var inputs []string
			var w Workload
			for i := 0; i < 2; i++ {
				var o Output
				w, o = rampFixture(t)
				if i == 1 {
					if test.noPassing {
						o.Summary.Steps = o.Summary.Steps[:1]
						o.Windows = o.Windows[:10]
					}
					if test.stop != "" {
						o.Summary.Steps[len(o.Summary.Steps)-1].Completed = false
					}
					s, err := Summarize(o.Summary.RunID, "gru", w, o.Summary.Steps, test.cleanup, test.stop)
					if err != nil {
						t.Fatal(err)
					}
					o.Summary = s
				}
				path := filepath.Join(t.TempDir(), "ramp.jsonl")
				if err := os.WriteFile(path, encodeOutput(t, o), 0600); err != nil {
					t.Fatal(err)
				}
				inputs = append(inputs, path)
			}
			r, _, err := Aggregate(inputs, w, "gru")
			if err != nil || r.Verdict != test.verdict || r.CapacityOpsPerS != 20 {
				t.Fatal(r.Verdict, r.CapacityOpsPerS, err)
			}
		})
	}
}
