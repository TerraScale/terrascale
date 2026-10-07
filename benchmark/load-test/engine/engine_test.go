package engine

import (
	"context"
	"errors"
	"math"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func config() Config {
	return Config{
		Rate: 20, Concurrency: 2, Duration: 500 * time.Millisecond,
		RequestTimeout: time.Second, DrainTimeout: 200 * time.Millisecond,
		Execute: func(context.Context, int, bool) error { return nil },
	}
}

func checkAccounting(t *testing.T, r Report) {
	t.Helper()
	if r.Planned != r.Started+r.Skipped+r.Dropped+r.NotScheduled {
		t.Fatalf("planned accounting: %+v", r)
	}
	if r.Started != r.SuccessfulReads+r.SuccessfulWrites+r.Errors+r.Unfinished {
		t.Fatalf("started accounting: %+v", r)
	}
	n := r.SuccessfulReads + r.SuccessfulWrites
	for _, latency := range []Latency{r.ServiceLatency, r.EndUserLatency} {
		if latency.All.Count != n || latency.Reads.Count != r.SuccessfulReads || latency.Writes.Count != r.SuccessfulWrites {
			t.Fatalf("successful sample accounting: %+v", r)
		}
	}
	if r.SchedulerLag.Count != r.Started || r.SchedulerLag.Mean > r.SchedulerLag.Max {
		t.Fatalf("lag accounting: %+v", r)
	}
	if r.Elapsed > 0 && r.SuccessfulOpsPerSecond != float64(n)/r.Elapsed.Seconds() {
		t.Fatalf("throughput must include the entire elapsed phase: %+v", r)
	}
}

func TestMixedIndicesAndErrors(t *testing.T) {
	cfg := config()
	var mu sync.Mutex
	seen := make(map[int]bool)
	cfg.Execute = func(_ context.Context, index int, write bool) error {
		mu.Lock()
		defer mu.Unlock()
		if _, exists := seen[index]; exists {
			t.Errorf("index %d retried", index)
		}
		seen[index] = write
		if write != (index%5 == 4) {
			t.Errorf("index %d selected write=%v", index, write)
		}
		if index == 2 || index == 4 {
			return errors.New("callback failure")
		}
		return nil
	}
	r, err := Run(context.Background(), cfg)
	if err != nil {
		t.Fatal(err)
	}
	checkAccounting(t, r)
	if r.Planned != 10 || r.Started != 10 || len(seen) != 10 || r.SuccessfulReads != 7 || r.SuccessfulWrites != 1 || r.Errors != 2 {
		t.Fatalf("expected deterministic 8 reads/2 writes, with one failure each: %+v", r)
	}
	if r.Elapsed < cfg.Duration || r.Aborted {
		t.Fatalf("fast callbacks must still measure a full phase: %+v", r)
	}
	if r.EndUserLatency.All.P50 < r.ServiceLatency.All.P50 || r.EndUserLatency.All.P99 < r.ServiceLatency.All.P99 {
		t.Fatalf("scheduled latency hid scheduling delay: %+v", r)
	}
}

func TestBatchedArrivalsKeepIndicesAndAccounting(t *testing.T) {
	cfg := config()
	cfg.Rate, cfg.Concurrency, cfg.Duration = MaxRate, 64, 100*time.Millisecond
	var mu sync.Mutex
	seen := make(map[int]bool)
	reads, writes, failures := 0, 0, 0
	cfg.Execute = func(_ context.Context, index int, write bool) error {
		mu.Lock()
		defer mu.Unlock()
		if _, exists := seen[index]; exists || index < 0 || index >= 1000 {
			t.Errorf("arrival index retried or outside phase: %d", index)
		}
		seen[index] = write
		if write != (index%5 == 4) {
			t.Errorf("arrival %d changed its offered operation", index)
		}
		if index%7 == 0 {
			failures++
			return errors.New("validation failure")
		}
		if write {
			writes++
		} else {
			reads++
		}
		return nil
	}
	r, err := Run(context.Background(), cfg)
	if err != nil {
		t.Fatal(err)
	}
	checkAccounting(t, r)
	if len(seen) == 0 || r.Started != len(seen) || r.SuccessfulReads != reads || r.SuccessfulWrites != writes || r.Errors != failures {
		t.Fatalf("batched callbacks disagree with report: %+v", r)
	}
}

func TestSaturationCannotPassOfferedRate(t *testing.T) {
	cfg := config()
	cfg.Rate, cfg.Duration = 1000, 60*time.Millisecond
	var active, peak atomic.Int32
	var mu sync.Mutex
	seen := make(map[int]bool)
	cfg.Execute = func(ctx context.Context, index int, write bool) error {
		n := active.Add(1)
		defer active.Add(-1)
		for old := peak.Load(); n > old && !peak.CompareAndSwap(old, n); old = peak.Load() {
		}
		mu.Lock()
		seen[index] = write
		mu.Unlock()
		timer := time.NewTimer(80 * time.Millisecond)
		defer timer.Stop()
		select {
		case <-timer.C:
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	r, err := Run(context.Background(), cfg)
	if err != nil {
		t.Fatal(err)
	}
	checkAccounting(t, r)
	if peak.Load() > int32(cfg.Concurrency) || r.Started == 0 || r.Started > cfg.Concurrency || r.Dropped+r.Skipped == 0 {
		t.Fatalf("unbounded or non-open-loop saturation: peak=%d, %+v", peak.Load(), r)
	}
	for index, write := range seen {
		if write != (index%5 == 4) {
			t.Fatalf("dropped slots changed mix: index=%d write=%v", index, write)
		}
	}
	if r.Elapsed < 80*time.Millisecond || r.SuccessfulOpsPerSecond >= 30 || r.MeetsThroughput(float64(cfg.Rate)*0.8) {
		t.Fatalf("saturated phase falsely passed throughput gate: %+v", r)
	}
}

func TestRequestTimeoutIsAnErrorEvenIfCallbackReturnsNil(t *testing.T) {
	cfg := config()
	cfg.Rate, cfg.Duration, cfg.RequestTimeout = 100, 20*time.Millisecond, 3*time.Millisecond
	cfg.Execute = func(ctx context.Context, _ int, _ bool) error {
		if _, ok := ctx.Deadline(); !ok {
			t.Error("callback missing request deadline")
		}
		<-ctx.Done()
		return nil
	}
	r, err := Run(context.Background(), cfg)
	if err != nil {
		t.Fatal(err)
	}
	checkAccounting(t, r)
	if r.Started == 0 || r.Errors != r.Started || r.Aborted || r.SuccessfulOpsPerSecond != 0 {
		t.Fatalf("request timeout falsely reported success: %+v", r)
	}
}

func TestExpiredDrainDoesNotAbortAlreadyCompletedWork(t *testing.T) {
	cfg := config()
	cfg.Duration, cfg.DrainTimeout = 100*time.Millisecond, time.Nanosecond
	r, err := Run(context.Background(), cfg)
	checkAccounting(t, r)
	if err != nil || r.Aborted || r.Started != 2 || r.SuccessfulReads != 2 || !r.MeetsThroughput(1) {
		t.Fatalf("idle drain deadline falsely aborted completed callbacks: err=%v report=%+v", err, r)
	}
}

func TestDrainDeadlineCancelsCooperativeCallback(t *testing.T) {
	cfg := config()
	cfg.Rate, cfg.Duration, cfg.DrainTimeout = 100, 10*time.Millisecond, 20*time.Millisecond
	exited := make(chan struct{})
	cfg.Execute = func(ctx context.Context, _ int, _ bool) error {
		defer close(exited)
		<-ctx.Done()
		return ctx.Err()
	}
	r, err := Run(context.Background(), cfg)
	checkAccounting(t, r)
	if !errors.Is(err, context.DeadlineExceeded) || !r.Aborted || r.Errors+r.Unfinished != 1 || r.SuccessfulOpsPerSecond != 0 {
		t.Fatalf("shared drain deadline: err=%v report=%+v", err, r)
	}
	select {
	case <-exited:
	case <-time.After(time.Second):
		t.Fatal("cooperative callback did not exit after drain deadline")
	}
}

func TestParentCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	cfg := config()
	cfg.Execute = func(ctx context.Context, _ int, _ bool) error {
		cancel()
		<-ctx.Done()
		return ctx.Err()
	}
	r, err := Run(ctx, cfg)
	checkAccounting(t, r)
	if !errors.Is(err, context.Canceled) || !r.Aborted || r.Started != 1 || r.Errors != 1 || r.NotScheduled != r.Planned-1 || r.Unfinished != 0 {
		t.Fatalf("cancelled phase accounting: err=%v report=%+v", err, r)
	}
	if r.Elapsed >= cfg.Duration || r.MeetsThroughput(0.01) {
		t.Fatalf("cancellation waited out the phase or passed gate: %+v", r)
	}
	// A pre-cancelled phase does not invoke the callback or allocate per-op data.
	r, err = Run(ctx, cfg)
	checkAccounting(t, r)
	if !errors.Is(err, context.Canceled) || r.Started != 0 || r.NotScheduled != r.Planned || !r.Aborted {
		t.Fatalf("pre-cancelled phase: err=%v report=%+v", err, r)
	}
}

func TestCancellationDuringDrain(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	cfg := config()
	cfg.Rate, cfg.Duration = 100, 10*time.Millisecond
	cfg.Execute = func(ctx context.Context, _ int, _ bool) error {
		timer := time.NewTimer(30 * time.Millisecond)
		defer timer.Stop()
		<-timer.C
		cancel()
		<-ctx.Done()
		return ctx.Err()
	}
	r, err := Run(ctx, cfg)
	checkAccounting(t, r)
	if !errors.Is(err, context.Canceled) || !r.Aborted || r.Errors != 1 || r.NotScheduled != 0 || r.Elapsed < 30*time.Millisecond {
		t.Fatalf("drain cancellation: err=%v report=%+v", err, r)
	}
}

func TestBoundedDrainWithIgnoringCallback(t *testing.T) {
	cfg := config()
	cfg.Rate, cfg.Duration, cfg.DrainTimeout = 100, 10*time.Millisecond, 20*time.Millisecond
	release, exited := make(chan struct{}), make(chan struct{})
	defer func() {
		close(release)
		select {
		case <-exited:
		case <-time.After(time.Second):
			t.Error("callback did not exit after release")
		}
	}()
	cfg.Execute = func(ctx context.Context, _ int, _ bool) error {
		defer close(exited)
		<-release // Deliberately violate the callback contract to test the bound.
		return ctx.Err()
	}
	r, err := Run(context.Background(), cfg)
	checkAccounting(t, r)
	if !errors.Is(err, context.DeadlineExceeded) || !r.Aborted || r.Unfinished != 1 || r.Errors != 0 || r.MeetsThroughput(0.01) {
		t.Fatalf("drain expiry: err=%v report=%+v", err, r)
	}
	if r.Elapsed < cfg.Duration+cfg.DrainTimeout || r.Elapsed > 200*time.Millisecond {
		t.Fatalf("drain is not bounded: %+v", r)
	}
}

func TestPercentilesAndSplits(t *testing.T) {
	values := make([]time.Duration, 100)
	for i := range values {
		values[i] = time.Duration(100-i) * time.Millisecond
	}
	if got := percentiles(values); got.Count != 100 || got.P50 != 50*time.Millisecond || got.P99 != 99*time.Millisecond {
		t.Fatalf("nearest rank: %+v", got)
	}
	if got := percentiles(nil); got != (Percentiles{}) {
		t.Fatalf("empty percentiles: %+v", got)
	}
	if got := percentiles([]time.Duration{7}); got != (Percentiles{1, 7, 7}) {
		t.Fatalf("singleton percentiles: %+v", got)
	}
	var s samples
	for _, d := range []time.Duration{4, 1, 3, 2} {
		s.add(false, d)
	}
	s.add(true, 100)
	got := s.summarize()
	if got.Reads != (Percentiles{4, 2, 4}) || got.Writes != (Percentiles{1, 100, 100}) || got.All != (Percentiles{5, 3, 100}) {
		t.Fatalf("split latency percentiles: %+v", got)
	}
}

func TestConfigValidationAndCap(t *testing.T) {
	for name, change := range map[string]func(*Config){
		"zero rate":            func(c *Config) { c.Rate = 0 },
		"excessive rate":       func(c *Config) { c.Rate = MaxRate + 1 },
		"zero concurrency":     func(c *Config) { c.Concurrency = 0 },
		"zero duration":        func(c *Config) { c.Duration = 0 },
		"zero request timeout": func(c *Config) { c.RequestTimeout = 0 },
		"zero drain":           func(c *Config) { c.DrainTimeout = 0 },
		"nil callback":         func(c *Config) { c.Execute = nil },
		"too many operations":  func(c *Config) { c.Rate, c.Duration = MaxRate, 300*time.Second+time.Nanosecond },
		"duration overflow":    func(c *Config) { c.DrainTimeout = time.Duration(math.MaxInt64) },
	} {
		t.Run(name, func(t *testing.T) {
			cfg := config()
			change(&cfg)
			r, err := Run(context.Background(), cfg)
			if err == nil || r.Planned != 0 || r.Started != 0 {
				t.Fatalf("invalid config accepted: err=%v report=%+v", err, r)
			}
		})
	}
	if _, err := Run(nil, config()); err == nil {
		t.Fatal("nil context accepted")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	for _, tc := range []struct {
		rate     int
		duration time.Duration
		planned  int
	}{
		{MaxRate, 300 * time.Second, MaxOperations},
		{1, time.Duration(MaxOperations) * time.Second, MaxOperations},
		{3, time.Second + time.Nanosecond, 4},
		{MaxRate, time.Nanosecond, 1},
	} {
		cfg := config()
		cfg.Rate, cfg.Duration = tc.rate, tc.duration
		r, err := Run(ctx, cfg)
		if !errors.Is(err, context.Canceled) || r.Planned != tc.planned {
			t.Fatalf("planned slots: rate=%d duration=%s err=%v report=%+v", tc.rate, tc.duration, err, r)
		}
	}
}

func TestThroughputGate(t *testing.T) {
	r := Report{SuccessfulOpsPerSecond: 100}
	if !r.MeetsThroughput(100) || r.MeetsThroughput(101) {
		t.Fatal("throughput threshold is not inclusive")
	}
	for _, minimum := range []float64{0, -1, math.NaN(), math.Inf(1)} {
		if r.MeetsThroughput(minimum) {
			t.Fatalf("invalid minimum accepted: %v", minimum)
		}
	}
	r.Aborted = true
	if r.MeetsThroughput(1) {
		t.Fatal("aborted phase passed throughput gate")
	}
	r.Aborted, r.Unfinished = false, 1
	if r.MeetsThroughput(1) {
		t.Fatal("unfinished phase passed throughput gate")
	}
}
