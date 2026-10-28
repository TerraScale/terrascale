package main

import (
	"context"
	"maps"
	"sync"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

// parityPlan is one closed-loop cell: unrecorded warmup, the measurement
// window, then a bounded drain for operations started inside the window.
type parityPlan struct {
	Concurrency            int
	Warmup, Measure, Drain time.Duration
}

// parityLive is the plan for live cells; only Concurrency (1 or 16) varies.
var parityLive = parityPlan{Warmup: 2 * time.Second, Measure: 5 * time.Second, Drain: 30 * time.Second}

// parityResult counts every operation that started inside the window. Unlike
// apibench.RunStep, which cancels at the window end and drops operations that
// finish after it, a slow operation keeps its full latency. Nil RPS or Latency
// means no measured operation completed: missing data, never zero.
type parityResult struct {
	Concurrency int               `json:"concurrency"`
	WarmupS     float64           `json:"warmup_s"`
	MeasureS    float64           `json:"measure_s"`
	DrainS      float64           `json:"drain_s"`
	Starts      int64             `json:"starts"`     // logical starts, warmup and window
	Measured    int64             `json:"measured"`   // started inside the window
	Unfinished  int64             `json:"unfinished"` // measured, cut by the drain deadline or cancellation
	OK          int64             `json:"ok"`
	Status      map[string]int64  `json:"status"`
	DurationS   float64           `json:"duration_s"` // window start to the last measured completion
	RPS         *float64          `json:"rps"`
	Latency     *apibench.Latency `json:"latency_us"`
	Canceled    bool              `json:"canceled"`
}

// parityMeasure runs plan.Concurrency workers until the window ends, then waits
// up to plan.Drain for them. At the drain deadline, or when ctx ends, it cancels
// the remaining operations and waits until every worker has returned, so no
// operation outlives the call. op must honor its context.
func parityMeasure(ctx context.Context, plan parityPlan, op func(context.Context) string) parityResult {
	opCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	recStart := time.Now().Add(plan.Warmup)
	recEnd := recStart.Add(plan.Measure)
	var (
		mu         sync.Mutex
		cut        bool
		last       time.Time
		starts     int64
		measured   int64
		unfinished int64
		ok         int64
		maxUS      int64
		status     = map[string]int64{}
		histogram  = hdrhistogram.New(1, 120_000_000, 3)
		wg         sync.WaitGroup
	)
	for range plan.Concurrency {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for opCtx.Err() == nil {
				started := time.Now()
				if !started.Before(recEnd) {
					return
				}
				inside := !started.Before(recStart)
				mu.Lock()
				starts++
				if inside {
					measured++
				}
				mu.Unlock()
				result := op(opCtx)
				finished := time.Now()
				if !inside {
					continue
				}
				mu.Lock()
				if cut {
					unfinished++
				} else {
					us := max(finished.Sub(started).Microseconds(), 1)
					maxUS = max(maxUS, us)
					_ = histogram.RecordValue(min(us, 120_000_000))
					status[result]++
					if result == "ok" {
						ok++
					}
					if finished.After(last) {
						last = finished
					}
				}
				mu.Unlock()
			}
		}()
	}
	done := make(chan struct{})
	go func() {
		wg.Wait()
		close(done)
	}()
	deadline := time.NewTimer(time.Until(recEnd.Add(plan.Drain)))
	defer deadline.Stop()
	select {
	case <-done:
	case <-deadline.C:
	case <-ctx.Done():
	}
	mu.Lock()
	cut = true
	mu.Unlock()
	cancel()
	<-done
	result := parityResult{
		Concurrency: plan.Concurrency, WarmupS: plan.Warmup.Seconds(), MeasureS: plan.Measure.Seconds(), DrainS: plan.Drain.Seconds(),
		Starts: starts, Measured: measured, Unfinished: unfinished, OK: ok, Status: maps.Clone(status), Canceled: ctx.Err() != nil,
	}
	if !last.IsZero() {
		if last.Before(recEnd) {
			last = recEnd
		}
		result.DurationS = last.Sub(recStart).Seconds()
		rps := float64(ok) / result.DurationS
		result.RPS = &rps
		result.Latency = &apibench.Latency{
			P50: float64(histogram.ValueAtQuantile(50)), P90: float64(histogram.ValueAtQuantile(90)),
			P99: float64(histogram.ValueAtQuantile(99)), P999: float64(histogram.ValueAtQuantile(99.9)),
			Max: float64(maxUS), Mean: histogram.Mean(),
		}
	}
	return result
}
