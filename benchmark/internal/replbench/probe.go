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

// probes measure replication lag while the load runs: each origin region writes one small document every period, and
// every other region is polled every 5 ms until it serves it. Lag is from the origin's acknowledgement to the first
// read that sees it, so it includes up to one poll interval and one read round trip.
type probes struct {
	wg      sync.WaitGroup
	lag     map[string]*Latency // "gru->sjc"
	missing map[string]*atomic.Int64
}

const probeDeadline = 2 * time.Minute

func startProbes(ctx context.Context, r *Runner, every time.Duration) *probes {
	p := &probes{lag: map[string]*Latency{}, missing: map[string]*atomic.Int64{}}
	for _, from := range r.regions {
		for _, to := range r.regions {
			if from != to {
				p.lag[from.Name+"->"+to.Name] = NewLatency()
				p.missing[from.Name+"->"+to.Name] = &atomic.Int64{}
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
				select {
				case <-ctx.Done():
					return
				case <-tick.C:
				}
				id := ulid.Make().String()
				resp, err := from.Do(ctx, http.MethodPut, DocPath(coll, id), []byte(`{"n":`+strconv.Itoa(n)+`}`), "application/json")
				if err != nil || resp.Status/100 != 2 {
					continue // a probe write the load pushed back is skipped, not measured
				}
				acked := time.Now()
				for _, to := range r.regions {
					if to == from {
						continue
					}
					key := from.Name + "->" + to.Name
					// Pollers outlive the load's end so the last probes still finish; they stop at their own deadline.
					p.wg.Go(func() {
						for time.Since(acked) < probeDeadline {
							resp, err := to.Do(context.WithoutCancel(ctx), http.MethodGet, DocPath(coll, id), nil, "")
							if err == nil && resp.Status == http.StatusOK {
								p.lag[key].Record(time.Since(acked))
								return
							}
							time.Sleep(5 * time.Millisecond)
						}
						p.missing[key].Add(1)
					})
				}
			}
		})
	}
	return p
}

func (p *probes) wait() { p.wg.Wait() }
