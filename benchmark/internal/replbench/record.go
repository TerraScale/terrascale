package replbench

import (
	"sync"
	"sync/atomic"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
)

// Latency is a concurrent HDR histogram in microseconds, 1 µs to 10 min at 3 significant digits.
type Latency struct {
	mu sync.Mutex
	h  *hdrhistogram.Histogram
}

func NewLatency() *Latency {
	return &Latency{h: hdrhistogram.New(1, int64(10*time.Minute/time.Microsecond), 3)}
}

func (l *Latency) Record(d time.Duration) {
	us := max(1, d.Microseconds())
	l.mu.Lock()
	_ = l.h.RecordValue(us)
	l.mu.Unlock()
}

// Summary is a histogram's count and percentiles in milliseconds.
type Summary struct {
	Count int64   `json:"count"`
	P50   float64 `json:"p50_ms"`
	P95   float64 `json:"p95_ms"`
	P99   float64 `json:"p99_ms"`
	Max   float64 `json:"max_ms"`
	Mean  float64 `json:"mean_ms"`
}

func (l *Latency) Summary() Summary {
	l.mu.Lock()
	defer l.mu.Unlock()
	ms := func(us int64) float64 { return float64(us) / 1000 }
	return Summary{
		Count: l.h.TotalCount(),
		P50:   ms(l.h.ValueAtQuantile(50)),
		P95:   ms(l.h.ValueAtQuantile(95)),
		P99:   ms(l.h.ValueAtQuantile(99)),
		Max:   ms(l.h.Max()),
		Mean:  l.h.Mean() / 1000,
	}
}

// Timeline counts events per second since its start, up to four hours.
type Timeline struct {
	start time.Time
	slots [4 * 3600]atomic.Int64
}

func NewTimeline(start time.Time) *Timeline { return &Timeline{start: start} }

func (t *Timeline) Add(at time.Time, n int64) {
	if s := int(at.Sub(t.start) / time.Second); s >= 0 && s < len(t.slots) {
		t.slots[s].Add(n)
	}
}

// Rates answers the per-second counts from the first to the last non-empty second.
func (t *Timeline) Rates() []int64 {
	first, last := -1, -1
	for i := range t.slots {
		if t.slots[i].Load() > 0 {
			if first < 0 {
				first = i
			}
			last = i
		}
	}
	if first < 0 {
		return nil
	}
	out := make([]int64, last-first+1)
	for i := range out {
		out[i] = t.slots[first+i].Load()
	}
	return out
}

// Counters are the outcomes of every call of one region.
type Counters struct {
	Throttled atomic.Int64 // 429 answers, each retried after Retry-After
	Retried   atomic.Int64 // transport errors, 5xx and region mismatches, each retried
	Failed    atomic.Int64 // calls that still failed after every retry
}
