package pitrbench

import (
	"context"
	"testing"
	"time"
)

// a7Run is a load-a of four batches on two streams, judged at T = 1000: batch 0 (stream 0) acknowledged at 400 and
// batch 1 (stream 1) at 900, before T; batch 2 (stream 0) sent at 950 and acknowledged at 1200, in flight; batch 3
// (stream 1) first sent at 1000, at T. events replaces a batch's lines; the restore holds the batches in present.
func a7Run(t *testing.T, events map[int64][]Event, present ...int64) *Run {
	t.Helper()
	r, db := newTestRun(t, 4000, 4000)
	lines := map[int64][]Event{
		0: {{Stream: 0, Ev: EvStart, Ms: 300}, {Stream: 0, Ev: EvAck, Ms: 400}},
		1: {{Stream: 1, Ev: EvStart, Ms: 800}, {Stream: 1, Ev: EvAck, Ms: 900}},
		2: {{Stream: 0, Ev: EvStart, Ms: 950}, {Stream: 0, Ev: EvAck, Ms: 1200}},
		3: {{Stream: 1, Ev: EvStart, Ms: 1000}, {Stream: 1, Ev: EvAck, Ms: 1300}},
	}
	for b, es := range events {
		lines[b] = es
	}
	for b := range int64(4) {
		for _, e := range lines[b] {
			e.Batch, e.Inv = b, "A"
			ledger(t, r, "load-a", e)
		}
	}
	for _, b := range present {
		putDocs(db, r, b*BatchSize, (b+1)*BatchSize, 1)
	}
	if err := r.AddCut(Cut{Name: "t", Kind: "bounded", Partial: "load-a", AtMs: 1000}); err != nil {
		t.Fatal(err)
	}
	return r
}

func TestNodeClockBoundsTheOffset(t *testing.T) {
	t.Parallel()
	r, _ := newTestRun(t, 4000, 4000)
	c, err := r.Client(time.Second)
	if err != nil {
		t.Fatal(err)
	}
	clock, date, err := c.Clock(context.Background(), time.Time{})
	// One host clock: the true offset 0 lies in the bound, which spans two seconds of Date header plus the round trip.
	if err != nil || clock.LoMs > 0 || clock.HiMs < 0 || clock.HiMs-clock.LoMs > 2500 || clock.Rollback {
		t.Fatalf("clock %+v: %v", clock, err)
	}
	if clock, _, err := c.Clock(context.Background(), date.Add(time.Hour)); err != nil || !clock.Rollback {
		t.Fatalf("a Date before the previous one: %+v %v", clock, err)
	}
}
