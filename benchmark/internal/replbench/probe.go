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

// probes measure replication lag while the load runs. Each origin region writes one small document every period; for
// every other region one poller reads the oldest probe it has not seen yet, every pollEvery, until that region serves
// it. Lag runs from the origin's acknowledgement to the first read that sees the probe, so it includes up to one poll
// interval and one read round trip. One read in flight per region pair keeps the probes from loading the cluster they
// measure; a probe that arrives before an older one is measured when the poller reaches it, which can only overstate
// its lag.
type probes struct {
	wg      sync.WaitGroup
	lag     map[string]*Latency // "gru->sjc"
	missing map[string]*atomic.Int64
}

const (
	probeDeadline = 2 * time.Minute
	pollEvery     = 10 * time.Millisecond
)

type probe struct {
	id    string
	acked time.Time
}

func startProbes(ctx context.Context, r *Runner, every time.Duration) *probes {
	p := &probes{lag: map[string]*Latency{}, missing: map[string]*atomic.Int64{}}
	queues := map[string]chan probe{}
	for _, from := range r.regions {
		for _, to := range r.regions {
			if from != to {
				key := from.Name + "->" + to.Name
				p.lag[key], p.missing[key] = NewLatency(), &atomic.Int64{}
				queues[key] = make(chan probe, 1<<16)
			}
		}
	}
	if every <= 0 {
		return p
	}
	coll := r.collection + "-probe"
	for _, from := range r.regions {
		p.wg.Go(func() {
			defer func() {
				for _, to := range r.regions {
					if to != from {
						close(queues[from.Name+"->"+to.Name])
					}
				}
			}()
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
					if to != from {
						queues[from.Name+"->"+to.Name] <- probe{id, acked}
					}
				}
			}
		})
		for _, to := range r.regions {
			if to == from {
				continue
			}
			key := from.Name + "->" + to.Name
			// The poller drains its queue after the load ends, so the last probes still finish; each has its own deadline.
			p.wg.Go(func() {
				for pr := range queues[key] {
					for {
						resp, err := to.Do(context.WithoutCancel(ctx), http.MethodGet, DocPath(coll, pr.id), nil, "")
						if err == nil && resp.Status == http.StatusOK {
							p.lag[key].Record(time.Since(pr.acked))
							break
						}
						if time.Since(pr.acked) > probeDeadline {
							p.missing[key].Add(1)
							break
						}
						time.Sleep(pollEvery)
					}
				}
			})
		}
	}
	return p
}

func (p *probes) wait() { p.wg.Wait() }
