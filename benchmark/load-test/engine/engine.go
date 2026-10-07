// Package engine runs bounded, open-loop 80% read / 20% write load phases.
package engine

import (
	"context"
	"errors"
	"math"
	"sort"
	"time"
)

const (
	MaxRate       = 10_000
	MaxOperations = 3_000_000
)

// Config describes one phase. All numeric fields must be positive. Execute
// must respect its context; Go cannot forcibly terminate an ignoring callback.
// Execute may run concurrently and must not retry operations.
type Config struct {
	Rate           int
	Concurrency    int
	Duration       time.Duration
	RequestTimeout time.Duration
	DrainTimeout   time.Duration
	Execute        func(context.Context, int, bool) error
}

type Percentiles struct {
	Count int
	P50   time.Duration
	P99   time.Duration
}

// Latency contains successful operations only, using nearest-rank percentiles.
type Latency struct {
	All    Percentiles
	Reads  Percentiles
	Writes Percentiles
}

// Lag measures dispatch time minus scheduled time, for every started operation.
type Lag struct {
	Count int
	Mean  time.Duration
	Max   time.Duration
}

type Report struct {
	Planned                int
	Started                int
	SuccessfulReads        int
	SuccessfulWrites       int
	Errors                 int
	Skipped                int // Scheduled slots missed by the scheduler.
	Dropped                int // Due operations rejected at the concurrency limit.
	NotScheduled           int // Remaining planned slots when parent cancellation stops scheduling.
	Unfinished             int // Started callbacks still outstanding at bounded drain expiry.
	Aborted                bool
	Elapsed                time.Duration // Whole phase plus drain, excluding percentile calculation.
	SuccessfulOpsPerSecond float64
	SchedulerLag           Lag
	ServiceLatency         Latency // Callback execution time.
	EndUserLatency         Latency // Scheduled time to successful callback completion.
}

// MeetsThroughput compares actual completed success throughput, including drain.
// An aborted phase never passes. Correctness/error gates belong to the caller.
func (r Report) MeetsThroughput(minimum float64) bool {
	return minimum > 0 && !math.IsNaN(minimum) && !math.IsInf(minimum, 0) &&
		!r.Aborted && r.Unfinished == 0 && r.SuccessfulOpsPerSecond >= minimum
}

type completion struct {
	write            bool
	service, endUser time.Duration
	err              error
	abortErr         error
}

type samples struct {
	reads, writes []time.Duration
}

func (s *samples) add(write bool, d time.Duration) {
	if write {
		s.writes = append(s.writes, d)
	} else {
		s.reads = append(s.reads, d)
	}
}

func (s *samples) summarize() Latency {
	reads, writes := percentiles(s.reads), percentiles(s.writes)
	return Latency{All: percentiles(append(s.reads, s.writes...)), Reads: reads, Writes: writes}
}

func percentiles(values []time.Duration) Percentiles {
	if len(values) == 0 {
		return Percentiles{}
	}
	sort.Slice(values, func(i, j int) bool { return values[i] < values[j] })
	n := len(values)
	return Percentiles{Count: n, P50: values[(n*50+99)/100-1], P99: values[(n*99+99)/100-1]}
}

// Run offers indices 0..Planned-1 on an absolute monotonic schedule. Index % 5
// == 4 selects writes, even after skips/drops. There is no request queue: at
// most Concurrency callbacks are outstanding. A wake dispatches at most 10 ms
// of due arrivals, preserving their scheduled times. Older arrivals are skipped
// rather than allowing an unbounded catch-up burst after a stalled scheduler.
//
// The drain ends no later than Duration + DrainTimeout after phase start.
// Parent cancellation cancels requests immediately, then permits at most
// DrainTimeout for their results. Individual callback errors/timeouts are
// counted, while cancellation or drain expiry also returns a non-nil error.
func Run(parent context.Context, cfg Config) (Report, error) {
	var r Report
	if parent == nil || cfg.Execute == nil || cfg.Rate < 1 || cfg.Rate > MaxRate ||
		cfg.Concurrency < 1 || cfg.Duration <= 0 || cfg.RequestTimeout <= 0 || cfg.DrainTimeout <= 0 {
		return r, errors.New("engine: context, callback, rate 1..10000, concurrency and positive durations required")
	}
	// Check before multiplication; this also bounds all retained latency data.
	if cfg.Duration > time.Duration(MaxOperations)*time.Second/time.Duration(cfg.Rate) {
		return r, errors.New("engine: phase exceeds 3000000 planned operations")
	}
	if cfg.DrainTimeout > time.Duration(math.MaxInt64)-cfg.Duration {
		return r, errors.New("engine: phase plus drain duration overflows")
	}
	r.Planned = int((int64(cfg.Duration)*int64(cfg.Rate) + int64(time.Second) - 1) / int64(time.Second))
	if err := parent.Err(); err != nil {
		r.Aborted, r.NotScheduled = true, r.Planned
		return r, err
	}
	limit := min(cfg.Concurrency, r.Planned)
	results := make(chan completion, limit)
	var service, endUser samples
	var runErr error
	pending := 0
	collect := func(c completion) {
		pending--
		if c.abortErr != nil {
			r.Aborted = true
			if runErr == nil {
				runErr = c.abortErr
			}
		}
		if c.err != nil {
			r.Errors++
			return
		}
		if c.write {
			r.SuccessfulWrites++
		} else {
			r.SuccessfulReads++
		}
		service.add(c.write, c.service)
		endUser.add(c.write, c.endUser)
	}
	collectReady := func() {
		for {
			select {
			case c := <-results:
				collect(c)
			default:
				return
			}
		}
	}
	start := time.Now() // time.Add/Sub/Until retain Go's monotonic clock.
	phaseEnd := start.Add(cfg.Duration)
	drainEnd := phaseEnd.Add(cfg.DrainTimeout)
	runCtx, cancel := context.WithDeadline(parent, drainEnd)
	defer cancel()
	runDeadline, _ := runCtx.Deadline()
	timer := time.NewTimer(0)
	defer timer.Stop()
	reset := func(at time.Time) {
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
		timer.Reset(time.Until(at))
	}
	var totalLag time.Duration
	batchLimit := max(1, (cfg.Rate+99)/100) // At most 10 ms of arrivals, or 100 at MaxRate.
	next := 0
schedule:
	for {
		select {
		case <-parent.Done():
			r.Aborted, runErr = true, parent.Err()
			r.NotScheduled = r.Planned - next
			drainEnd = minTime(drainEnd, time.Now().Add(cfg.DrainTimeout))
		case c := <-results:
			collect(c)
			continue
		case <-timer.C:
			if err := parent.Err(); err != nil {
				r.Aborted, runErr = true, err
				r.NotScheduled = r.Planned - next
				drainEnd = minTime(drainEnd, time.Now().Add(cfg.DrainTimeout))
				break schedule
			}
			collectReady()
			now := time.Now()
			if !now.Before(phaseEnd) {
				r.Skipped += r.Planned - next
				break schedule
			}
			due := min(r.Planned-1, int(now.Sub(start)*time.Duration(cfg.Rate)/time.Second))
			first := max(next, due-batchLimit+1)
			r.Skipped += first - next
			next = first
			for index := first; index <= due; index++ {
				if err := parent.Err(); err != nil {
					r.Aborted, runErr = true, err
					r.NotScheduled = r.Planned - next
					drainEnd = minTime(drainEnd, time.Now().Add(cfg.DrainTimeout))
					break schedule
				}
				collectReady()
				scheduled := start.Add(time.Duration(int64(index) * int64(time.Second) / int64(cfg.Rate)))
				next = index + 1
				if pending == limit {
					r.Dropped++
				} else {
					lag := time.Since(scheduled)
					totalLag += lag
					r.SchedulerLag.Max = max(r.SchedulerLag.Max, lag)
					r.Started++
					pending++
					go func(index int, scheduled time.Time) {
						ctx, stop := context.WithTimeout(runCtx, cfg.RequestTimeout)
						defer stop()
						began := time.Now()
						write := index%5 == 4
						err := cfg.Execute(ctx, index, write)
						finished := time.Now()
						if err == nil {
							err = ctx.Err()
							// Timer delivery can lag the deadline under CPU pressure.
							if deadline, ok := ctx.Deadline(); err == nil && ok && !finished.Before(deadline) {
								err = context.DeadlineExceeded
							}
						}
						abortErr := runCtx.Err()
						if abortErr == nil && !finished.Before(runDeadline) {
							abortErr = context.DeadlineExceeded
						}
						results <- completion{
							write: write, service: finished.Sub(began), endUser: finished.Sub(scheduled),
							err: err, abortErr: abortErr,
						}
					}(index, scheduled)
				}
			}
			at := phaseEnd
			if next < r.Planned {
				at = start.Add(time.Duration(int64(next) * int64(time.Second) / int64(cfg.Rate)))
			}
			reset(at)
			continue
		}
		break
	}
	reset(drainEnd)
drain:
	for pending > 0 {
		select {
		case c := <-results:
			collect(c)
		case <-parent.Done():
			if runErr == nil {
				r.Aborted, runErr = true, parent.Err()
				drainEnd = minTime(drainEnd, time.Now().Add(cfg.DrainTimeout))
				reset(drainEnd)
			}
			// Do not repeatedly select an already closed parent channel.
			parent = context.Background()
		case <-timer.C:
			collectReady()
			if pending > 0 {
				r.Aborted = true
				if runErr == nil {
					runErr = context.DeadlineExceeded
				}
				cancel()
			}
			break drain
		}
	}
	r.Elapsed = time.Since(start)
	r.Unfinished = pending
	r.SuccessfulOpsPerSecond = float64(r.SuccessfulReads+r.SuccessfulWrites) / r.Elapsed.Seconds()
	r.SchedulerLag.Count = r.Started
	if r.Started > 0 {
		r.SchedulerLag.Mean = totalLag / time.Duration(r.Started)
	}
	r.ServiceLatency, r.EndUserLatency = service.summarize(), endUser.summarize()
	return r, runErr
}

func minTime(a, b time.Time) time.Time {
	if a.Before(b) {
		return a
	}
	return b
}
