package benchmark

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"github.com/oklog/ulid/v2"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func fixture() Workload {
	return Workload{Name: "point-reads", DurationS: 1, Concurrency: 4, Dataset: Dataset{Documents: 1, DocSizeBytes: 32}, Mix: []Mix{{Op: "doc.get", Weight: 100}}, Targets: Targets{P99MS: 100}}
}
func TestWorkloads(t *testing.T) {
	t.Parallel()
	files, e := filepath.Glob("../../workloads/*.json")
	files = slices.DeleteFunc(files, func(f string) bool { return filepath.Base(f) == "api-bench.json" })
	if e != nil || len(files) != 6 {
		t.Fatal(files, e)
	}
	for _, file := range files {
		w, e := Load(file)
		if e != nil {
			t.Fatal(file, e)
		}
		counts := map[string]int{}
		for i := 0; i < 100; i++ {
			counts[w.Pick(i).Op]++
		}
		for _, m := range w.Mix {
			if counts[m.Op] != m.Weight {
				t.Fatal(w.Name, counts)
			}
		}
	}
	w := fixture()
	w.Mix[0].Weight = 99
	if w.Validate() == nil {
		t.Fatal("accepted mix !=100")
	}
	w = fixture()
	w.Ramp = &Ramp{StepS: int(^uint(0) >> 1), Concurrency: []int{1, 2}}
	if w.Validate() == nil {
		t.Fatal("accepted overflowing ramp")
	}
}
func TestAdmissionAccountingAndCancellation(t *testing.T) {
	t.Parallel()
	w := fixture()
	w.Concurrency = 1
	w.Targets.P99MS = 1
	var active, peak atomic.Int32
	step, e := RunPhase(context.Background(), w, 1, 40*time.Millisecond, 0, func(ctx context.Context, _ int, _ Mix) error {
		n := active.Add(1)
		defer active.Add(-1)
		peak.Store(max(peak.Load(), n))
		select {
		case <-time.After(20 * time.Millisecond):
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}, nil, "gru", "test")
	if e != nil || !step.Completed || step.ElapsedS < .04 || peak.Load() > 1 {
		t.Fatal(step, e, peak.Load())
	}
	s := step.PerOp["doc.get"]
	if s.Count != 40 || s.Errors < 30 || s.Valid() != nil {
		t.Fatal(s.Count, s.Errors)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	start := time.Now()
	step, e = RunPhase(ctx, w, 1, time.Second, 0, func(context.Context, int, Mix) error { t.Error("called canceled callback"); return nil }, nil, "gru", "test")
	if e == nil || step.Completed || time.Since(start) > 100*time.Millisecond {
		t.Fatal(step, e)
	}
}
func TestLatencyUsesIntendedStartAndDrain(t *testing.T) {
	t.Parallel()
	w := fixture()
	var windows []Window
	step, e := RunPhase(context.Background(), w, 4, 80*time.Millisecond, 0, func(context.Context, int, Mix) error { time.Sleep(15 * time.Millisecond); return nil }, func(r Window) error { windows = append(windows, r); return nil }, "sfo", "test")
	if e != nil || step.PerOp["doc.get"].P50US < 15000 {
		t.Fatal(step, e)
	}
	var count int64
	for _, r := range windows {
		count += r.Count
	}
	if count != step.PerOp["doc.get"].Count || step.ElapsedS < .08 {
		t.Fatal(windows, step)
	}
}
func TestSchedulerWaitsForNextSlotAfterStall(t *testing.T) {
	t.Parallel()
	w := fixture()
	type arrival struct {
		index int
		start time.Time
	}
	arrivals := make(chan arrival, 128)
	step, err := RunPhase(context.Background(), w, 4, 1100*time.Millisecond, 0, func(_ context.Context, index int, _ Mix) error {
		arrivals <- arrival{index, time.Now()}
		return nil
	}, func(Window) error {
		// A flush stalls admission between two 25 ms scheduled slots.
		time.Sleep(35 * time.Millisecond)
		return nil
	}, "sfo", "test")
	if err != nil || !step.Completed {
		t.Fatal(step, err)
	}
	close(arrivals)
	for a := range arrivals {
		if a.index == 40 {
			t.Fatal("dispatched the one-second slot after the 35 ms emission stall")
		}
		intended := step.Start.Add(time.Duration(a.index) * 25 * time.Millisecond)
		if a.start.Before(intended) {
			t.Fatalf("arrival %d started %s before its scheduled slot", a.index, intended.Sub(a.start))
		}
	}
}

func TestRampStopsOnErrors(t *testing.T) {
	t.Parallel()
	w := fixture()
	w.Name = "ramp"
	w.DurationS = 10
	w.Ramp = &Ramp{StepS: 5, Concurrency: []int{4, 8}}
	began := time.Now()
	step, e := RunPhase(context.Background(), w, 4, 5*time.Second, 0, func(context.Context, int, Mix) error { return errors.New("failure") }, nil, "gru", "test")
	if e == nil || e.Error() != "ramp safety threshold exceeded" || step.Completed || time.Since(began) > 2*time.Second {
		t.Fatal(step, e)
	}
}
func output(t *testing.T, region string, us int64) ([]byte, Workload) {
	t.Helper()
	w := fixture()
	a := newAccumulator()
	for i := 0; i < 100; i++ {
		a.record(us, nil)
	}
	stats := a.stats(true)
	start := time.Date(2026, 10, 8, 0, 0, 0, 0, time.UTC)
	step := Step{Concurrency: 4, Start: start, End: start.Add(time.Second), ElapsedS: 1, OpsPerS: 100, PerOp: map[string]Stats{"doc.get": stats}, Completed: true}
	summary, e := Summarize(ulid.Make().String(), region, w, []Step{step}, true, "")
	if e != nil {
		t.Fatal(e)
	}
	b := new(bytes.Buffer)
	enc := json.NewEncoder(b)
	stats.Histogram = nil
	_ = enc.Encode(Window{TS: start, Client: "go", SDK: "ts-go", SDKVersion: "test", Region: region, Op: "doc.get", Stats: stats, Concurrency: 4, ElapsedS: 1})
	_ = enc.Encode(summary)
	return b.Bytes(), w
}
func TestJSONLAndHistogramValidation(t *testing.T) {
	t.Parallel()
	raw, w := output(t, "gru", 1000)
	if _, e := read(bytes.NewReader(raw), w, "gru"); e != nil {
		t.Fatal(e)
	}
	for _, bad := range []string{strings.Replace(string(raw), `"count":100`, `"count":99`, 1), strings.Replace(string(raw), `"region":"gru"`, `"region":"sfo"`, 1), strings.Replace(string(raw), `"ops_per_s":100`, `"ops_per_s":200`, 1), string(raw) + "{}\n", strings.SplitN(string(raw), "\n", 2)[0] + "\n"} {
		if _, e := read(strings.NewReader(bad), w, "gru"); e == nil {
			t.Fatal("accepted corrupted JSONL")
		}
	}
	a := newAccumulator()
	a.record(100, nil)
	stats := a.stats(true)
	stats.Histogram.Counts[0] = -1
	if stats.Valid() == nil {
		t.Fatal("accepted negative histogram")
	}
}
func TestAggregationMergesHistograms(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	var inputs []string
	var w Workload
	for i, us := range []int64{1000, 100000} {
		b, workload := output(t, "gru", us)
		w = workload
		name := filepath.Join(dir, string(rune('a'+i))+".jsonl")
		if e := os.WriteFile(name, b, 0600); e != nil {
			t.Fatal(e)
		}
		inputs = append(inputs, name)
	}
	r, _, e := Aggregate(inputs, w, "gru")
	if e != nil {
		t.Fatal(e)
	}
	if r.OpsPerS != 200 || r.PerOp["doc.get"].P99US < 100000 || r.PerOp["doc.get"].P99US > 101000 || r.Metrics.Status != "unavailable" || r.Stored {
		t.Fatal(r)
	}
	if !strings.Contains(r.Markdown(), "| doc.get | 200 | 0 |") || !strings.Contains(r.Markdown(), "unavailable") {
		t.Fatal(r.Markdown())
	}
	if _, _, e = Aggregate([]string{inputs[0], inputs[0]}, w, "gru"); e == nil {
		t.Fatal("accepted duplicate run")
	}
}
func TestEmitterFailureAndInvalidPhase(t *testing.T) {
	t.Parallel()
	w := fixture()
	step, e := RunPhase(context.Background(), w, 4, 20*time.Millisecond, 0, func(context.Context, int, Mix) error { return nil }, func(Window) error { return errors.New("disk full") }, "gru", "test")
	if e == nil || step.Completed {
		t.Fatal(step, e)
	}
	if _, e = RunPhase(nil, w, 4, time.Second, 0, nil, nil, "gru", "test"); e == nil {
		t.Fatal("nil context/callback accepted")
	}
}

func TestRejectsFalseCompletionAndTiming(t *testing.T) {
	t.Parallel()
	raw, w := output(t, "gru", 1000)
	lines := bytes.Split(bytes.TrimSpace(raw), []byte("\n"))
	var s Summary
	if e := json.Unmarshal(lines[1], &s); e != nil {
		t.Fatal(e)
	}
	s.Start = s.Start.Add(-time.Second)
	body, _ := json.Marshal(s)
	bad := append(append(append([]byte{}, lines[0]...), '\n'), body...)
	if _, e := read(bytes.NewReader(bad), w, "gru"); e == nil {
		t.Fatal("accepted summary timestamp different from phase")
	}
	if e := json.Unmarshal(lines[1], &s); e != nil {
		t.Fatal(e)
	}
	s.Steps[0].Completed = false
	body, _ = json.Marshal(s)
	dir := t.TempDir()
	path := filepath.Join(dir, "partial.jsonl")
	bad = append(append(append([]byte{}, lines[0]...), '\n'), body...)
	if e := os.WriteFile(path, bad, 0600); e != nil {
		t.Fatal(e)
	}
	r, _, e := Aggregate([]string{path}, w, "gru")
	if e != nil {
		t.Fatal(e)
	}
	if r.Verdict != "FAIL" {
		t.Fatal("incomplete phase passed")
	}
	if e := json.Unmarshal(lines[1], &s); e != nil {
		t.Fatal(e)
	}
	s.Steps[0].ElapsedS = .5
	body, _ = json.Marshal(s)
	bad = append(append(append([]byte{}, lines[0]...), '\n'), body...)
	if _, e := read(bytes.NewReader(bad), w, "gru"); e == nil {
		t.Fatal("accepted shortened completed phase")
	}
}
