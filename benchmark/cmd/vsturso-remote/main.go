package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"math/rand/v2"
	"net/http"
	"os"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type cell struct {
	Lane                string
	Repeat              int
	Size                string
	Workload            string
	Concurrency         int
	OpsPerSecond        float64
	P50Micros           float64
	P99Micros           float64
	CpuMicrosPerOp      float64
	AllocatedBytesPerOp int64
	Errors              int64
}

type size struct {
	name  string
	bytes int
}

func main() {
	var (
		targetsPath = flag.String("targets", "", "targets JSON (required)")
		region      = flag.String("region", "sjc", "region whose client API is driven")
		lane        = flag.String("lane", "api-live", "lane name in the output")
		collection  = flag.String("collection", "vsturso", "collection the documents live in (created if absent)")
		keys        = flag.Int("keys", 2000, "keys per size, all written before any cell")
		seconds     = flag.Int("seconds", 5, "measured seconds per cell, after a 1 s warm-up")
		repeats     = flag.Int("repeats", 3, "repeats of every cell")
		workloads   = flag.String("workloads", "write,read,mixed", "workloads to run")
		concs       = flag.String("concurrency", "1,16", "closed-loop workers per cell, comma-separated")
		preloaders  = flag.Int("preloaders", 4, "concurrent writers of the preload (a Hobby plan admits 4 requests at once)")
		rate        = flag.Float64("rate", 0, "most operations per second across workers (0 = unpaced); keeps a run inside the tenant's plan budget")
	)
	flag.Parse()
	t, err := benchclient.LoadTargets(*targetsPath)
	if err != nil {
		log.Fatal(err)
	}
	r, err := benchclient.NewRegion(*region, t, 64)
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()
	body, _ := json.Marshal(map[string]string{"name": *collection})
	if resp, err := r.Do(ctx, http.MethodPost, "/collections", body, "application/json"); err != nil || (resp.Status/100 != 2 && resp.Status != http.StatusConflict) {
		log.Fatalf("create collection: %v %d %s", err, resp.Status, resp.Body)
	}
	sizes := []size{{"small", 256}, {"medium", 4096}}
	for _, s := range sizes {

		pace := pacer(*rate)
		run(*preloaders, time.Time{}, func(worker int, i *int64) bool {
			pace()
			key := int(atomic.AddInt64(i, 1)) - 1
			if key >= *keys {
				return false
			}

			for attempt := 1; ; attempt++ {
				err := put(ctx, r, *collection, id(s, key), doc(s, int64(key)))
				if err == nil {
					break
				}
				if !errors.Is(err, errBusy) || attempt == 10 {
					log.Fatalf("preload: %v", err)
				}
				time.Sleep(time.Second)
			}
			return true
		})
	}
	fmt.Fprintf(os.Stderr, "preloaded %d keys per size\n", *keys)
	for rep := 1; rep <= *repeats; rep++ {
		for _, s := range sizes {
			for _, w := range strings.Split(*workloads, ",") {
				for _, field := range strings.Split(*concs, ",") {
					var conc int
					if _, err := fmt.Sscan(field, &conc); err != nil || conc < 1 {
						log.Fatalf("bad -concurrency %q", *concs)
					}
					c := measure(ctx, r, *collection, s, w, conc, *keys, time.Duration(*seconds)*time.Second, pacer(*rate))
					c.Lane, c.Repeat = *lane, rep
					line, _ := json.Marshal(c)
					fmt.Println(string(line))
				}
			}
		}
	}
}

func run(n int, deadline time.Time, step func(worker int, counter *int64) bool) {
	var wg sync.WaitGroup
	var counter int64
	for w := range n {
		wg.Go(func() {
			for (deadline.IsZero() || time.Now().Before(deadline)) && step(w, &counter) {
			}
		})
	}
	wg.Wait()
}

func pacer(rate float64) func() {
	if rate <= 0 {
		return func() {}
	}
	var mu sync.Mutex
	next := time.Now()
	return func() {
		mu.Lock()
		at := next
		if now := time.Now(); now.After(next) {
			next = now
		}
		next = next.Add(time.Duration(float64(time.Second) / rate))
		mu.Unlock()
		time.Sleep(time.Until(at))
	}
}

func measure(ctx context.Context, r *benchclient.Region, coll string, s size, workload string, conc, keys int, d time.Duration, pace func()) cell {
	start := time.Now()
	from, end := start.Add(time.Second), start.Add(time.Second+d)
	lat := make([][]time.Duration, conc)
	var errs atomic.Int64
	rngs := make([]*rand.Rand, conc)
	for w := range rngs {
		rngs[w] = rand.New(rand.NewPCG(uint64(w)*7919+uint64(conc), 1))
	}
	run(conc, end, func(w int, _ *int64) bool {
		pace()
		rng := rngs[w]
		key := rng.IntN(keys)
		write := workload == "write" || (workload == "mixed" && rng.IntN(100) < 20)
		began := time.Now()
		var err error
		if write {
			err = put(ctx, r, coll, id(s, key), doc(s, int64(w)<<40|began.UnixNano()&(1<<40-1)))
		} else {
			err = get(ctx, r, coll, id(s, key))
		}
		if began.Before(from) {
			return true
		}
		if err != nil {
			errs.Add(1)
		} else if time.Now().Before(end) {
			lat[w] = append(lat[w], time.Since(began))
		}
		return true
	})
	all := slices.Concat(lat...)
	slices.Sort(all)
	q := func(p float64) float64 {
		if len(all) == 0 {
			return 0
		}
		return float64(all[min(len(all)-1, int(p*float64(len(all))))].Microseconds())
	}
	return cell{Size: s.name, Workload: workload, Concurrency: conc, OpsPerSecond: float64(len(all)) / d.Seconds(),
		P50Micros: q(0.50), P99Micros: q(0.99), Errors: errs.Load()}
}

func put(ctx context.Context, r *benchclient.Region, coll, key string, body []byte) error {
	resp, err := r.Do(ctx, http.MethodPut, benchclient.DocPath(coll, key), body, "application/json")
	if err == nil && resp.Status == http.StatusTooManyRequests {
		err = fmt.Errorf("PUT %s: %w", key, errBusy)
	} else if err == nil && resp.Status/100 != 2 {
		err = fmt.Errorf("PUT %s answered %d: %s", key, resp.Status, resp.Body)
	}
	return err
}

var errBusy = errors.New("tenant busy")

func get(ctx context.Context, r *benchclient.Region, coll, key string) error {
	resp, err := r.Do(ctx, http.MethodGet, benchclient.DocPath(coll, key), nil, "")
	if err == nil && resp.Status/100 != 2 {
		err = fmt.Errorf("GET %s answered %d", key, resp.Status)
	}
	return err
}

func id(s size, key int) string {
	return fmt.Sprintf("01JZZZZZZZZZZZ%c%011d", s.name[0]-'a'+'A', key)
}

func doc(s size, counter int64) []byte {
	head := fmt.Sprintf(`{"name":"Ada Lovelace","n":"%013d","tags":["alpha","beta","gamma"],"score":12.5,"active":true,"pad":"`, counter%10_000_000_000_000)
	return []byte(head + strings.Repeat("x", s.bytes-len(head)-2) + `"}`)
}
