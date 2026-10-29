package benchmark

import (
	"context"
	"testing"
	"time"
)

func TestCompletionBucketsSurviveSlowEmission(t *testing.T) {
	t.Parallel()
	w := fixture()
	w.Targets.P99MS = 1000
	completed := make(chan time.Time, 32)
	var windows []Window
	step, err := RunPhase(context.Background(), w, 4, 2300*time.Millisecond, 0, func(ctx context.Context, _ int, _ Mix) error {
		select {
		case <-time.After(650 * time.Millisecond):
			completed <- time.Now()
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}, func(row Window) error {
		windows = append(windows, row)
		if len(windows) == 1 {
			// Completions queue in seconds one and two while the writer stalls.
			time.Sleep(1100 * time.Millisecond)
		}
		return nil
	}, "gru", "test")
	if err != nil || !step.Completed {
		t.Fatal(step, err)
	}
	close(completed)
	expected := map[int]int64{}
	for ts := range completed {
		expected[int(ts.Sub(step.Start)/time.Second)]++
	}
	if expected[1] == 0 {
		t.Fatal("fixture had no completions during the emission stall")
	}
	for _, row := range windows {
		i := int(row.TS.Sub(step.Start) / time.Second)
		if row.Count-row.Errors != expected[i] {
			t.Fatalf("second %d: %d successes, want %d by completion time", i, row.Count-row.Errors, expected[i])
		}
		delete(expected, i)
	}
	if len(expected) != 0 {
		t.Fatal("missing completion buckets", expected)
	}
}
