package replbench

import (
	"context"
	"net/http"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/oklog/ulid/v2"
)

// probes measure replication lag while the load runs. Each origin region writes one small document, then reads it from
// every other region every pollEvery until each serves it, and writes the next probe once all have (at most one per
// period). Lag runs from the origin's acknowledgement to the first read that sees the probe, so it includes up to one
// poll interval and one read round trip. One probe in flight per origin keeps the probes from loading the cluster they
// measure and from queueing behind slow reads, which would add the queueing to the lag.
type probes struct {
	wg      sync.WaitGroup
	lag     map[string]*Latency // "gru->sjc"
	missing map[string]*atomic.Int64
	read    *Latency // every poll read: the resolution of the lag numbers
}

const (
	probeDeadline = 2 * time.Minute
	pollEvery     = 10 * time.Millisecond
)

func startProbes(ctx context.Context, r *Runner, every time.Duration) *probes {
	p := &probes{lag: map[string]*Latency{}, missing: map[string]*atomic.Int64{}, read: NewLatency()}
	for _, from := range r.regions {
		for _, to := range r.regions {
			if from != to {
				p.lag[from.Name+"->"+to.Name], p.missing[from.Name+"->"+to.Name] = NewLatency(), &atomic.Int64{}
			}
		}
	}
	if every <= 0 {
		return p
	}
	coll := r.collection + "-probe"
	for _, from := range r.regions {
		p.wg.Go(func() {
			tick := time.NewTicker(every)
			defer tick.Stop()
			for n := 0; ; n++ {
				// The first probe goes at once, so even a short load measures each pair.
				if n > 0 {
					select {
					case <-ctx.Done():
						return
					case <-tick.C:
					}
				}
				id := ulid.Make().String()
				resp, err := from.Do(ctx, http.MethodPut, DocPath(coll, id), []byte(`{"n":`+strconv.Itoa(n)+`}`), "application/json")
				if err != nil || resp.Status/100 != 2 {
					continue // a probe write the load pushed back is skipped, not measured
				}
				acked := time.Now()
				var seen sync.WaitGroup
				for _, to := range r.regions {
					if to == from {
						continue
					}
					key := from.Name + "->" + to.Name
					// The last probe still finishes after the load ends; it stops at its own deadline.
					seen.Go(func() {
						for {
							began := time.Now()
							resp, err := to.Do(context.WithoutCancel(ctx), http.MethodGet, DocPath(coll, id), nil, "")
							p.read.Record(time.Since(began))
							if err == nil && resp.Status == http.StatusOK {
								p.lag[key].Record(time.Since(acked))
								return
							}
							if time.Since(acked) > probeDeadline {
								p.missing[key].Add(1)
								return
							}
							time.Sleep(pollEvery)
						}
					})
				}
				seen.Wait()
			}
		})
	}
	return p
}

func (p *probes) wait() { p.wg.Wait() }
