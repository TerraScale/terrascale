package main

import (
	"context"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/ts-benchmark/internal/apibench"
)

var parityTestPlan = parityPlan{Warmup: 100 * time.Millisecond, Measure: 300 * time.Millisecond, Drain: 10 * time.Second}

// An operation that starts inside the window and ends after it keeps its full
// latency, and throughput is measured to its completion. RunStep drops it.
func TestParityMeasureKeepsTailsPastTheWindow(t *testing.T) {
	t.Parallel()
	var active atomic.Int64
	var slow atomic.Bool
	plan := parityTestPlan
	plan.Concurrency = 1
	began := time.Now()
	result := parityMeasure(context.Background(), plan, func(ctx context.Context) string {
		active.Add(1)
		defer active.Add(-1)
		delay := 10 * time.Millisecond
		if time.Since(began) > 150*time.Millisecond && !slow.Swap(true) { // inside the 100–400 ms window
			delay = 700 * time.Millisecond
		}
		select {
		case <-time.After(delay):
			return "ok"
		case <-ctx.Done():
			return "canceled"
		}
	})
	if active.Load() != 0 {
		t.Fatal("operation outlived the measurement")
	}
	if result.Unfinished != 0 || result.Canceled || result.Latency == nil || result.RPS == nil {
		t.Fatalf("result %+v", result)
	}
	if result.Latency.Max < 700_000 || result.Latency.P999 < 690_000 {
		t.Fatalf("tail censored: %+v", *result.Latency)
	}
	if result.DurationS < 0.7 || result.OK != result.Measured || result.Measured >= result.Starts {
		t.Fatalf("window accounting %+v", result)
	}
}

// The drain deadline cancels an operation that never finishes, reports it as
// unfinished and returns only after it has returned.
func TestParityMeasureBoundsTheDrain(t *testing.T) {
	t.Parallel()
	var active atomic.Int64
	plan := parityTestPlan
	plan.Concurrency, plan.Drain = 16, 200*time.Millisecond
	started := time.Now()
	result := parityMeasure(context.Background(), plan, func(ctx context.Context) string {
		active.Add(1)
		defer active.Add(-1)
		if time.Since(started) > 150*time.Millisecond { // every worker blocks inside the window
			<-ctx.Done()
			return "canceled"
		}
		time.Sleep(time.Millisecond)
		return "ok"
	})
	elapsed := time.Since(started)
	if active.Load() != 0 {
		t.Fatal("operation outlived the measurement")
	}
	if result.Unfinished != 16 || result.Canceled || elapsed > 2*time.Second || elapsed < 600*time.Millisecond {
		t.Fatalf("unfinished %d canceled %v after %v", result.Unfinished, result.Canceled, elapsed)
	}
}

// Caller cancellation stops network work at once, and an empty window stays missing data.
func TestParityMeasureStopsOnCancellation(t *testing.T) {
	t.Parallel()
	var active atomic.Int64
	plan := parityTestPlan
	plan.Concurrency, plan.Warmup = 4, time.Hour
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	result := parityMeasure(ctx, plan, func(ctx context.Context) string {
		active.Add(1)
		defer active.Add(-1)
		<-ctx.Done()
		return "canceled"
	})
	if active.Load() != 0 || !result.Canceled || result.Measured != 0 || result.RPS != nil || result.Latency != nil {
		t.Fatalf("result %+v", result)
	}
}

// Control: the shared RunStep drops the same slow operation that parityMeasure keeps.
func TestParityMeasureKeepsWhatRunStepCensors(t *testing.T) {
	t.Parallel()
	slowOnce := func() func(context.Context) string {
		began := time.Now()
		var slow atomic.Bool
		return func(ctx context.Context) string {
			delay := 10 * time.Millisecond
			if time.Since(began) > 300*time.Millisecond && !slow.Swap(true) {
				delay = 1200 * time.Millisecond
			}
			select {
			case <-time.After(delay):
				return "ok"
			case <-ctx.Done():
				return "canceled"
			}
		}
	}
	shared := slowOnce()
	step := apibench.Step{Type: "step", Scenario: "doc.get", Mode: "closed", Concurrency: 1, StepS: 1, AbortErrorRate: 1}
	censored := apibench.RunStep(context.Background(), step, func(ctx context.Context, _, _ int) string { return shared(ctx) }, &apibench.Meter{})
	kept := parityMeasure(context.Background(), parityPlan{Concurrency: 1, Measure: time.Second, Drain: 10 * time.Second}, slowOnce())
	if censored.Latency.Max >= 1_000_000 || kept.Latency == nil || kept.Latency.Max < 1_200_000 || kept.Unfinished != 0 {
		t.Fatalf("RunStep max %v µs, parity %+v", censored.Latency.Max, kept)
	}
}
