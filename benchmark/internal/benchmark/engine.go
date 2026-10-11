package benchmark

import (
	"context"
	"errors"
	"math"
	"sync"
	"time"
)

type Execute func(context.Context, int, Mix) error
type completion struct {
	op       string
	intended time.Time
	end      time.Time
	err      error
}

// RunPhase preserves TS-721's absolute schedule, bounded admission and timed
// drain. Unadmitted arrivals are errors; they never disappear from throughput.
func RunPhase(parent context.Context, w Workload, concurrency int, duration time.Duration, step int, execute Execute, emit func(Window) error, region, sdkVersion string) (Step, error) {
	if parent == nil || execute == nil || concurrency < 1 || concurrency > 256 || duration <= 0 || duration > 600*time.Second {
		return Step{}, errors.New("invalid phase context, callback, concurrency or duration")
	}
	if err := w.Validate(); err != nil {
		return Step{}, err
	}
	if err := parent.Err(); err != nil {
		return Step{}, err
	}
	// Allocate measurement storage before starting the offered-load schedule.
	total, bucket := map[string]*accumulator{}, map[string]*accumulator{}
	for _, m := range w.Mix {
		total[m.Op] = newAccumulator()
		bucket[m.Op] = newAccumulator()
	}
	completions := make(chan completion, concurrency)
	batch := make([]completion, 0, concurrency)
	var completionMu sync.Mutex
	start := time.Now()
	end := start.Add(duration)
	result := Step{Concurrency: concurrency, Start: start, PerOp: map[string]Stats{}}
	rate := min(10000, max(1, int(math.Round(float64(concurrency)*1000/w.Targets.P99MS))))
	interval := time.Second / time.Duration(rate)
	ctx, cancel := context.WithDeadline(parent, end.Add(5*time.Second))
	defer cancel()
	buckets := map[int]map[string]*accumulator{0: bucket}
	pending, index, window := 0, 0, 0
	next := start
	windowStart := start
	windowEnd := start.Add(time.Second)
	var stopped error
	record := func(op string, intended, finished time.Time, err error) {
		us := finished.Sub(intended).Microseconds()
		total[op].record(us, err)
		i := int(finished.Sub(start) / time.Second)
		if buckets[i] == nil {
			buckets[i] = map[string]*accumulator{}
		}
		if buckets[i][op] == nil {
			buckets[i][op] = newAccumulator()
		}
		buckets[i][op].record(us, err)
	}
	accept := func(c completion) {
		pending--
		record(c.op, c.intended, c.end, c.err)
	}
	collect := func() time.Time {
		batch = batch[:0]
		completionMu.Lock()
		for {
			select {
			case c := <-completions:
				batch = append(batch, c)
			default:
				// Producers timestamp and publish under this lock, so no earlier
				// completion can arrive after buckets through this time are closed.
				now := time.Now()
				completionMu.Unlock()
				for _, c := range batch {
					accept(c)
				}
				return now
			}
		}
	}
	flush := func(at time.Time) error {
		current := buckets[window]
		unsafe := w.Ramp != nil && rampSafetyExceeded(current, w.Targets.P99MS)
		for _, m := range w.Mix {
			var stats Stats
			if a := current[m.Op]; a != nil {
				stats = a.stats(false)
			}
			if emit != nil {
				if err := emit(Window{TS: windowStart.UTC(), Client: "go", SDK: "ts-go", SDKVersion: sdkVersion, Region: region, Op: m.Op, Stats: stats, Step: step, Concurrency: concurrency, ElapsedS: at.Sub(windowStart).Seconds()}); err != nil {
					// A partially written bucket cannot safely be retried.
					emit = nil
					return err
				}
			}
		}
		if unsafe && stopped == nil {
			stopped = errors.New("ramp safety threshold exceeded")
		}
		delete(buckets, window)
		window++
		windowStart = at
		windowEnd = at.Add(time.Second)
		return nil
	}
	timer := time.NewTimer(0)
	defer timer.Stop()
	reset := func(at time.Time) {
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
		timer.Reset(max(0, time.Until(at)))
	}
loop:
	for {
		if err := parent.Err(); err != nil {
			stopped = err
			break
		}
		now := collect()
		for !now.Before(windowEnd) {
			if err := flush(windowEnd); err != nil {
				stopped = err
				break loop
			}
			if stopped != nil {
				break loop
			}
		}
		// Emission and histogram processing may stall the scheduler.
		now = time.Now()
		if !now.Before(end) {
			break
		}
		if !now.Before(windowEnd) {
			continue
		}
		if next.Before(end) && !now.Before(next) {
			for now.Sub(next) > 10*time.Millisecond && next.Before(end) {
				m := w.Pick(index)
				record(m.Op, next, now, errors.New("scheduler missed arrival"))
				index++
				next = start.Add(time.Duration(index) * interval)
			}
			now = time.Now()
			if !now.Before(end) {
				break
			}
			if !now.Before(windowEnd) {
				continue
			}
			if !next.Before(end) || now.Before(next) || now.Sub(next) > 10*time.Millisecond {
				continue
			}
			if err := parent.Err(); err != nil {
				stopped = err
				break
			}
			m := w.Pick(index)
			intended := next
			index++
			next = start.Add(time.Duration(index) * interval)
			if pending == concurrency {
				record(m.Op, intended, now, errors.New("concurrency admission full"))
				continue
			}
			pending++
			go func(i int, m Mix, intended time.Time) {
				request, stop := context.WithTimeout(ctx, 5*time.Second)
				defer stop()
				err := execute(request, i, m)
				completionMu.Lock()
				finished := time.Now()
				if err == nil {
					err = request.Err()
					if deadline, ok := request.Deadline(); ok && !finished.Before(deadline) {
						err = context.DeadlineExceeded
					}
				}
				// The channel has room for every admitted request.
				completions <- completion{m.Op, intended, finished, err}
				completionMu.Unlock()
			}(index-1, m, intended)
			continue
		}
		reset(minTime(minTime(next, end), windowEnd))
		select {
		case <-parent.Done():
			stopped = parent.Err()
			break loop
		case c := <-completions:
			accept(c)
		case <-timer.C:
		}
	}
	if stopped != nil {
		cancel()
	} else {
		for next.Before(end) {
			m := w.Pick(index)
			record(m.Op, next, time.Now(), errors.New("scheduler missed arrival"))
			index++
			next = start.Add(time.Duration(index) * interval)
		}
	}
	for pending > 0 {
		now := collect()
		if pending == 0 {
			break
		}
		for !now.Before(windowEnd) {
			if err := flush(windowEnd); err != nil {
				if stopped == nil {
					stopped = err
					cancel()
				}
				break
			}
			if stopped != nil {
				cancel()
			}
		}
		reset(windowEnd)
		select {
		case c := <-completions:
			accept(c)
		case <-timer.C:
		}
	}
	finished := collect()
	for !finished.Before(windowEnd) {
		if err := flush(windowEnd); err != nil {
			if stopped == nil {
				stopped = err
			}
			break
		}
	}
	if finished.After(windowStart) {
		if err := flush(finished); err != nil && stopped == nil {
			stopped = err
		}
	}
	var count, failed int64
	for op, a := range total {
		result.PerOp[op] = a.stats(true)
		count += a.count
		failed += a.errors
	}
	result.End = finished.UTC()
	result.ElapsedS = finished.Sub(start).Seconds()
	result.OpsPerS = float64(count-failed) / result.ElapsedS
	if count > 0 {
		result.ErrorRate = float64(failed) / float64(count)
	}
	result.Completed = stopped == nil
	return result, stopped
}

func rampSafetyExceeded(bucket map[string]*accumulator, targetMS float64) bool {
	var count, failures int64
	for _, a := range bucket {
		count += a.count
		failures += a.errors
		if float64(a.h.ValueAtQuantile(99)) > targetMS*2000 {
			return true
		}
	}
	return count > 0 && float64(failures)/float64(count) > .01
}

func minTime(a, b time.Time) time.Time {
	if a.Before(b) {
		return a
	}
	return b
}

func Summarize(runID, region string, w Workload, steps []Step, cleanup bool, stopped string) (Summary, error) {
	s := Summary{Type: "summary", Client: "go", Region: region, Workload: w.Name, WorkloadConfig: w, RunID: runID, Steps: steps, PerOp: map[string]Stats{}, Cleanup: cleanup, Stopped: stopped}
	if len(steps) == 0 {
		return s, errors.New("no measured steps")
	}
	s.Start = steps[0].Start
	s.End = steps[len(steps)-1].End
	s.ElapsedS = s.End.Sub(s.Start).Seconds()
	if s.ElapsedS <= 0 || s.ElapsedS > 900 {
		return s, errors.New("invalid summary wall-clock interval")
	}
	counts := map[string]*accumulator{}
	for _, step := range steps {
		for op, v := range step.PerOp {
			if err := v.Valid(); err != nil {
				return s, err
			}
			a := counts[op]
			if a == nil {
				a = newAccumulator()
				counts[op] = a
			}
			a.count += v.Count
			a.errors += v.Errors
			if a.h.Merge(importHistogram(v)) != 0 {
				return s, errors.New("histogram merge dropped samples")
			}
		}
	}
	var n, e int64
	for op, a := range counts {
		s.PerOp[op] = a.stats(true)
		n += a.count
		e += a.errors
	}
	s.OpsPerS = float64(n-e) / s.ElapsedS
	if n > 0 {
		s.ErrorRate = float64(e) / float64(n)
	}
	return s, nil
}
