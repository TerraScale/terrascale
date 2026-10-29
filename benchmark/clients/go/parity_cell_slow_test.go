//go:build slow

package main

import (
	"context"
	"fmt"
	"net/http"
	"testing"
	"time"
)

// Measuring a warmed-up concurrency matrix is a timing scenario. Keep every
// accounting and protocol assertion here with a window suitable for race builds.
func TestSlowParityCellAcceptsExactReadsOnEveryLane(t *testing.T) {
	t.Parallel()
	for _, lane := range []string{"raw-h1", "raw-h2", "sdk"} {
		for _, concurrency := range []int{1, 16} {
			t.Run(fmt.Sprintf("%s-c%d", lane, concurrency), func(t *testing.T) {
				t.Parallel()
				target := parityTestTarget(lane)
				f := newParityFixture(t, func(_ int64, w http.ResponseWriter, r *http.Request) { serveDocument(target, "gru", w, r) })
				plan := parityPlan{Warmup: 100 * time.Millisecond, Measure: 3 * time.Second, Drain: 10 * time.Second}
				plan.Concurrency = concurrency
				row, err := parityCell(context.Background(), parityTestToken("a.b.c"), f.transport(), target, plan)
				if err != nil || len(row.Rejected) != 0 || row.Transport.Attempts != row.Starts || row.Measured == 0 {
					t.Fatalf("row %+v, %v", row, err)
				}
				if lane == "raw-h1" && row.Transport.H1 != row.Starts || lane != "raw-h1" && row.Transport.H2 != row.Starts {
					t.Fatalf("protocol counts %+v", row.Transport)
				}
			})
		}
	}
}
