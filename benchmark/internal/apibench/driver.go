package apibench

import (
	"context"
	"io"
	"maps"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
)

// Op runs one operation and returns "ok" or its status key.
type Op func(ctx context.Context, worker, i int) string

type clockKey struct{}

// Restart makes the driver time the current closed-loop op from now, so setup
// before the measured call (such as the create before a delete) is not timed.
// Open-loop latency always counts from the intended arrival, so it is ignored.
func Restart(ctx context.Context) {
	if c, ok := ctx.Value(clockKey{}).(*time.Time); ok {
		*c = time.Now()
	}
}

// Meter counts request and response body bytes through wrapped transports.
type Meter struct {
	out, in atomic.Int64
}

func (m *Meter) Wrap(rt http.RoundTripper) http.RoundTripper {
	return meter{rt, m}
}

type meter struct {
	rt http.RoundTripper
	m  *Meter
}

func (t meter) RoundTrip(r *http.Request) (*http.Response, error) {
	if r.Body != nil && r.Body != http.NoBody {
		r = r.Clone(r.Context())
		r.Body = counter{r.Body, &t.m.out}
	}
	resp, err := t.rt.RoundTrip(r)
	if err != nil {
		return nil, err
	}
	resp.Body = counter{resp.Body, &t.m.in}
	return resp, nil
}

type counter struct {
	io.ReadCloser
	n *atomic.Int64
}

func (c counter) Read(p []byte) (int, error) {
	n, err := c.ReadCloser.Read(p)
	c.n.Add(int64(n))
	return n, err
}

// RunStep runs warmup_s unrecorded, then step_s recorded. An op counts only if
// it starts and finishes inside the recorded window.
func RunStep(ctx context.Context, s Step, op Op, m *Meter) Result {
	t0 := time.Now()
	recStart := t0.Add(time.Duration(s.WarmupS) * time.Second)
	recEnd := recStart.Add(time.Duration(s.StepS) * time.Second)
	runCtx, cancel := context.WithDeadline(ctx, recEnd)
	defer cancel()
	rec := newRecorder(s, recStart, recEnd)
	var wg sync.WaitGroup
	if s.Mode == "open" {
		wg.Add(1)
		go func() {
			defer wg.Done()
			arrivals(runCtx, s, op, rec, &wg, t0)
		}()
	} else {
		for w := range s.Concurrency {
			wg.Add(1)
			go func(w int) {
				defer wg.Done()
				var start time.Time
				ctx := context.WithValue(runCtx, clockKey{}, &start)
				for i := 0; runCtx.Err() == nil; i++ {
					start = time.Now()
					status := op(ctx, w, i)
					rec.observe(start, time.Now(), status)
				}
			}(w)
		}
	}
	sleepUntil(ctx, recStart)
	cpu0, out0, in0 := cpuSeconds(), m.out.Load(), m.in.Load()
	for at := recStart; at.Before(recEnd); {
		at = at.Add(time.Second)
		if at.After(recEnd) {
			at = recEnd
		}
		if !sleepUntil(ctx, at) || rec.check() {
			break
		}
	}
	cpu1, out1, in1 := cpuSeconds(), m.out.Load(), m.in.Load()
	rec.close(time.Now())
	cancel()
	drained := make(chan struct{})
	go func() {
		wg.Wait()
		close(drained)
	}()
	select {
	case <-drained:
	case <-time.After(5 * time.Second):
	}
	res := rec.result()
	res.BytesOut, res.BytesIn = out1-out0, in1-in0
	res.CPUS = cpu1 - cpu0
	res.RSSMB = rssMB()
	return res
}

// arrivals issues open-loop arrivals at t0 + i/rate. An arrival that finds all
// workers busy is recorded as dropped. A late arrival runs at once and its
// latency counts from its intended time, so queueing delay is not hidden.
func arrivals(ctx context.Context, s Step, op Op, rec *recorder, wg *sync.WaitGroup, t0 time.Time) {
	ids := make(chan int, s.Concurrency)
	for w := range s.Concurrency {
		ids <- w
	}
	end := t0.Add(time.Duration(s.WarmupS+s.StepS) * time.Second)
	for i := 0; ; i++ {
		at := t0.Add(time.Duration(i) * time.Second / time.Duration(s.Rate))
		if !at.Before(end) || !sleepUntil(ctx, at) {
			return
		}
		select {
		case w := <-ids:
			wg.Add(1)
			go func(i, w int, at time.Time) {
				defer wg.Done()
				status := op(ctx, w, i)
				rec.observe(at, time.Now(), status)
				ids <- w
			}(i, w, at)
		default:
			rec.observe(at, time.Now(), "dropped")
		}
	}
}

type recorder struct {
	s          Step
	mu         sync.Mutex
	start, end time.Time
	ops, ok    int64
	status     map[string]int64
	total      *hdrhistogram.Histogram
	window     *hdrhistogram.Histogram
	stopped    string
}

func newRecorder(s Step, start, end time.Time) *recorder {
	return &recorder{
		s:      s,
		start:  start,
		end:    end,
		status: map[string]int64{},
		total:  hdrhistogram.New(1, 60_000_000, 3),
		window: hdrhistogram.New(1, 60_000_000, 3),
	}
}

func (r *recorder) observe(start, finish time.Time, status string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if start.Before(r.start) || !finish.Before(r.end) {
		return
	}
	r.ops++
	r.status[status]++
	if status == "ok" {
		r.ok++
		us := min(max(finish.Sub(start).Microseconds(), 1), 60_000_000)
		r.total.RecordValue(us)
		r.window.RecordValue(us)
	}
}

// check ends the recorded window now if the error rate or the last second's p99
// breaches its threshold. The error rate is cumulative since the window started;
// each call resets the one-second histogram that the p99 comes from.
func (r *recorder) check() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	p99 := r.window.ValueAtQuantile(99)
	r.window.Reset()
	switch {
	case r.ops >= 20 && float64(r.ops-r.ok)/float64(r.ops) > r.s.AbortErrorRate:
		r.stopped = "error rate"
	case r.s.AbortP99US > 0 && p99 > r.s.AbortP99US:
		r.stopped = "p99"
	default:
		return false
	}
	r.end = time.Now()
	return true
}

func (r *recorder) close(at time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if at.Before(r.end) {
		r.end = at
	}
}

func (r *recorder) result() Result {
	r.mu.Lock()
	defer r.mu.Unlock()
	d := r.end.Sub(r.start).Seconds()
	res := Result{
		Type:         "result",
		Scenario:     r.s.Scenario,
		Transport:    r.s.Transport,
		PayloadBytes: r.s.PayloadBytes,
		Mode:         r.s.Mode,
		Concurrency:  r.s.Concurrency,
		Rate:         r.s.Rate,
		Start:        r.start.UTC(),
		DurationS:    d,
		Ops:          r.ops,
		OK:           r.ok,
		Errors:       r.ops - r.ok,
		Status:       maps.Clone(r.status),
		Stopped:      r.stopped,
		Latency: Latency{
			P50:  float64(r.total.ValueAtQuantile(50)),
			P90:  float64(r.total.ValueAtQuantile(90)),
			P99:  float64(r.total.ValueAtQuantile(99)),
			P999: float64(r.total.ValueAtQuantile(99.9)),
			Max:  float64(r.total.Max()),
			Mean: r.total.Mean(),
		},
	}
	if r.ops > 0 {
		res.ErrorRate = float64(res.Errors) / float64(r.ops)
	}
	if d > 0 {
		res.RPS = float64(r.ok) / d
	}
	return res
}

func sleepUntil(ctx context.Context, at time.Time) bool {
	t := time.NewTimer(time.Until(at))
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
	return ctx.Err() == nil
}

func cpuSeconds() float64 {
	var ru syscall.Rusage
	_ = syscall.Getrusage(syscall.RUSAGE_SELF, &ru)
	return tv(ru.Utime) + tv(ru.Stime)
}

func tv(t syscall.Timeval) float64 {
	return float64(t.Sec) + float64(t.Usec)/1e6
}

func rssMB() float64 {
	b, err := os.ReadFile("/proc/self/statm")
	if err != nil {
		return 0
	}
	f := strings.Fields(string(b))
	if len(f) < 2 {
		return 0
	}
	pages, _ := strconv.ParseInt(f[1], 10, 64)
	return float64(pages*int64(os.Getpagesize())) / (1 << 20)
}
