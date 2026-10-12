package replbench

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"fmt"
	mrand "math/rand/v2"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/oklog/ulid/v2"
)

// SizeWeight is one document size of the load and its share of the documents.
type SizeWeight struct {
	Bytes  int
	Weight int
}

// ParseSizes reads "256:70,1024:25,16384:5" (bytes:weight); a lone "256" is every document at 256 bytes.
func ParseSizes(s string) ([]SizeWeight, error) {
	var out []SizeWeight
	for part := range strings.SplitSeq(s, ",") {
		b, w, found := strings.Cut(strings.TrimSpace(part), ":")
		bytes, err := strconv.Atoi(b)
		weight := 1
		if found {
			weight, err = strconv.Atoi(w)
		}
		if err != nil || bytes < 1 || bytes > 900*1024 || weight < 1 {
			return nil, fmt.Errorf("sizes: %q is not bytes[:weight] with 1 <= bytes <= 921600", part)
		}
		out = append(out, SizeWeight{bytes, weight})
	}
	return out, nil
}

// Config is one run of the load.
type Config struct {
	Docs           int           // documents created across every region, split evenly
	Sizes          []SizeWeight  // document sizes and their shares
	Batch          int           // documents per request: 1 is one PUT, more is one transaction of puts
	Concurrency    int           // writers per region
	DeletePct      float64       // share of created documents the creating region deletes right after
	ConflictDocs   int           // documents every region overwrites concurrently (same field and own field)
	ConflictRounds int           // overwrites of each conflict document per region
	ProbeEvery     time.Duration // one lag probe per origin region this often
	Converge       time.Duration // longest wait for every region to match after the load
	Seed           uint64
}

func (c Config) sizeOf(i int) int {
	total := 0
	for _, s := range c.Sizes {
		total += s.Weight
	}
	pick := mrand.New(mrand.NewPCG(c.Seed^0x5eed, uint64(i))).IntN(total)
	for _, s := range c.Sizes {
		if pick < s.Weight {
			return s.Bytes
		}
		pick -= s.Weight
	}
	return c.Sizes[len(c.Sizes)-1].Bytes
}

func (c Config) deletes(i int) bool {
	return c.DeletePct > 0 && mrand.New(mrand.NewPCG(c.Seed^0xde1e7e, uint64(i))).Float64() < c.DeletePct/100
}

// Runner holds the regions and everything one run records.
type Runner struct {
	cfg        Config
	targets    Targets
	regions    []*Region
	collection string
	log        func(format string, args ...any)

	mu       sync.Mutex
	expected map[string]Hash // live documents and the hash every region must hold
	deleted  int
	unknown  map[string]bool // writes whose outcome is unknown after every retry: left out of the check, reported

	create, remove, patch map[string]*Latency
	counters              map[string]*Counters
	timeline              map[string]*Timeline
	created               map[string]*atomic.Int64
	conflicts             conflictBook
	probes                *probes
}

// NewRunner connects to every region of t; the first region in order (gru when present) seeds shared state.
func NewRunner(cfg Config, t Targets, log func(string, ...any)) (*Runner, error) {
	names := make([]string, 0, len(t.Regions))
	for name := range t.Regions {
		names = append(names, name)
	}
	slices.SortFunc(names, func(a, b string) int {
		// gru first, as the home region of the e2e suite; the rest in name order.
		switch {
		case a == "gru":
			return -1
		case b == "gru":
			return 1
		}
		return strings.Compare(a, b)
	})
	r := &Runner{cfg: cfg, targets: t, log: log, expected: map[string]Hash{}, unknown: map[string]bool{},
		create: map[string]*Latency{}, remove: map[string]*Latency{}, patch: map[string]*Latency{},
		counters: map[string]*Counters{}, timeline: map[string]*Timeline{}, created: map[string]*atomic.Int64{}}
	for _, name := range names {
		reg, err := NewRegion(name, t, cfg.Concurrency+8)
		if err != nil {
			return nil, err
		}
		r.regions = append(r.regions, reg)
		r.create[name], r.remove[name], r.patch[name] = NewLatency(), NewLatency(), NewLatency()
		r.counters[name] = &Counters{}
		r.created[name] = &atomic.Int64{}
	}
	return r, nil
}

// call sends one write until it is answered: 429 waits Retry-After, transport errors, 5xx and region mismatches retry
// with backoff up to six times. ok is the final 2xx; known is false when the write may or may not have applied.
func (r *Runner) call(ctx context.Context, reg *Region, method, path string, body []byte, ctype string, accept ...int) (resp Response, ok, known bool) {
	c := r.counters[reg.Name]
	for attempt := 0; ; {
		resp, err := reg.Do(ctx, method, path, body, ctype)
		switch {
		case ctx.Err() != nil:
			return resp, false, false
		case err == nil && resp.Status == http.StatusTooManyRequests:
			c.Throttled.Add(1)
			sleep(ctx, max(resp.RetryAfter, 100*time.Millisecond))
			continue
		case err == nil && (resp.Status/100 == 2 || slices.Contains(accept, resp.Status)):
			return resp, true, true
		case err == nil && resp.Status/100 == 4:
			c.Failed.Add(1)
			return resp, false, true // refused before applying
		}
		attempt++
		if attempt > 6 {
			c.Failed.Add(1)
			return resp, false, false
		}
		c.Retried.Add(1)
		sleep(ctx, time.Duration(50<<attempt)*time.Millisecond)
	}
}

func sleep(ctx context.Context, d time.Duration) {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
}

// Setup creates the run's collections in the first region and waits until every region serves them.
func (r *Runner) Setup(ctx context.Context) error {
	r.collection = "bench-" + strings.ToLower(ulid.Make().String())
	for _, c := range []string{r.collection, r.collection + "-probe", r.collection + "-conflict"} {
		body, _ := json.Marshal(map[string]string{"name": c})
		if resp, ok, _ := r.call(ctx, r.regions[0], http.MethodPost, "/collections", body, "application/json"); !ok {
			return fmt.Errorf("create collection %s: HTTP %d %s", c, resp.Status, resp.Body)
		}
		for _, reg := range r.regions[1:] {
			if err := until(ctx, 2*time.Minute, func() bool {
				resp, err := reg.Do(ctx, http.MethodGet, "/collections/"+c, nil, "")
				return err == nil && resp.Status == http.StatusOK
			}); err != nil {
				return fmt.Errorf("collection %s on %s: %w", c, reg.Name, err)
			}
		}
	}
	r.log("collections %s{,-probe,-conflict} served by %d regions", r.collection, len(r.regions))
	return nil
}

func until(ctx context.Context, limit time.Duration, done func() bool) error {
	deadline := time.Now().Add(limit)
	for !done() {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("not within %s", limit)
		}
		sleep(ctx, 20*time.Millisecond)
	}
	return nil
}

// Load writes every document and the conflict rounds, with lag probes running alongside, and answers its wall time.
func (r *Runner) Load(ctx context.Context) (time.Duration, error) {
	if err := r.seedConflicts(ctx); err != nil {
		return 0, err
	}
	start := time.Now()
	for _, reg := range r.regions {
		r.timeline[reg.Name] = NewTimeline(start)
	}
	probeCtx, stopProbes := context.WithCancel(ctx)
	r.probes = startProbes(probeCtx, r, r.cfg.ProbeEvery)
	var wg sync.WaitGroup
	for ri, reg := range r.regions {
		next := atomic.Int64{}
		next.Store(int64(ri))
		for range r.cfg.Concurrency {
			wg.Go(func() { r.writer(ctx, reg, &next) })
		}
		wg.Go(func() { r.conflictWriter(ctx, reg) })
	}
	progress := time.NewTicker(10 * time.Second)
	done := make(chan struct{})
	go func() { wg.Wait(); close(done) }()
loop:
	for {
		select {
		case <-done:
			break loop
		case <-progress.C:
			var parts []string
			for _, reg := range r.regions {
				parts = append(parts, fmt.Sprintf("%s=%d", reg.Name, r.created[reg.Name].Load()))
			}
			r.log("load %s: created %s", time.Since(start).Round(time.Second), strings.Join(parts, " "))
		}
	}
	progress.Stop()
	elapsed := time.Since(start)
	stopProbes()
	r.probes.wait()
	return elapsed, ctx.Err()
}

// writer takes this region's documents (index = region + k * regions) in batches until none are left.
func (r *Runner) writer(ctx context.Context, reg *Region, next *atomic.Int64) {
	step := int64(len(r.regions))
	entropy := ulid.Monotonic(rand.Reader, 0)
	local := map[string]Hash{}
	unknown := map[string]bool{}
	deleted := 0
	defer func() {
		r.mu.Lock()
		for id, h := range local {
			r.expected[id] = h
		}
		for id := range unknown {
			r.unknown[id] = true
		}
		r.deleted += deleted
		r.mu.Unlock()
	}()
	type doc struct {
		i    int
		id   string
		body []byte
	}
	batch := make([]doc, 0, r.cfg.Batch)
	for ctx.Err() == nil {
		batch = batch[:0]
		for len(batch) < r.cfg.Batch {
			i := int(next.Add(step) - step)
			if i >= r.cfg.Docs {
				break
			}
			batch = append(batch, doc{i, ulid.MustNew(ulid.Now(), entropy).String(), Body(r.cfg.Seed, i, reg.Name, r.cfg.sizeOf(i))})
		}
		if len(batch) == 0 {
			return
		}
		began := time.Now()
		var ok, known bool
		if len(batch) == 1 {
			_, ok, known = r.call(ctx, reg, http.MethodPut, DocPath(r.collection, batch[0].id), batch[0].body, "application/json")
		} else {
			ops := make([]json.RawMessage, len(batch))
			for k, d := range batch {
				ops[k], _ = json.Marshal(map[string]any{"op": "put", "collection": r.collection, "id": d.id, "document": json.RawMessage(d.body)})
			}
			body, _ := json.Marshal(map[string]any{"ops": ops})
			_, ok, known = r.call(ctx, reg, http.MethodPost, "/transactions", body, "application/json")
		}
		finished := time.Now()
		r.create[reg.Name].Record(finished.Sub(began))
		for _, d := range batch {
			switch {
			case ok:
				local[d.id] = DocHash(d.id, d.body) // Body is canonical JSON already.
			case !known:
				unknown[d.id] = true
			}
		}
		if ok {
			r.created[reg.Name].Add(int64(len(batch)))
			r.timeline[reg.Name].Add(finished, int64(len(batch)))
		}
		for _, d := range batch {
			if !ok || !r.cfg.deletes(d.i) {
				continue
			}
			began := time.Now()
			_, gone, known := r.call(ctx, reg, http.MethodDelete, DocPath(r.collection, d.id), nil, "", http.StatusNotFound)
			r.remove[reg.Name].Record(time.Since(began))
			switch {
			case gone:
				delete(local, d.id)
				deleted++
			case !known:
				delete(local, d.id)
				unknown[d.id] = true
			}
		}
	}
}
