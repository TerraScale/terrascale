// Command repl-converge checks that concurrent writes to one document from two regions converge to the same value in
// every region, and that the survivor is the last writer by (HLC, node) as the changes feed reports the two writes.
//
//	repl-converge --targets cell.json --docs 50 > run.jsonl
//
// For each pair of regions and each document it PUTs {"w":"<region>-<n>"} to both regions at the same instant, polls
// every region until all serve identical bytes, then reads every region's changes feed for the two writes' stamps and
// compares the survivor with LWW. Each output line is one document; the last is the run's summary.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

type doc struct {
	Pair        string             `json:"pair"`
	ID          string             `json:"id"`
	Status      map[string]int     `json:"status"`
	AckMs       map[string]float64 `json:"ack_ms"`
	ConvergedMs float64            `json:"converged_ms"` // from the later acknowledgement to every region serving the same bytes
	Survivor    string             `json:"survivor"`
	LWW         string             `json:"lww"` // the origin LWW picks from the feed's stamps
	Stamps      map[string]string  `json:"stamps"`
	OK          bool               `json:"ok"`
	Error       string             `json:"error,omitempty"`
}

func main() {
	targetsPath := flag.String("targets", "", "targets JSON (replbench format, every region)")
	collection := flag.String("collection", "lww", "collection, created if absent")
	n := flag.Int("docs", 50, "documents per region pair")
	converge := flag.Duration("converge", time.Minute, "longest wait for the regions to agree on one document")
	flag.Parse()
	t, err := replbench.LoadTargets(*targetsPath)
	if err != nil {
		log.Fatal(err)
	}
	names := slices.Sorted(func(yield func(string) bool) {
		for name := range t.Regions {
			if !yield(name) {
				return
			}
		}
	})
	regions := map[string]*replbench.Region{}
	for _, name := range names {
		if regions[name], err = replbench.NewRegion(name, t, 8); err != nil {
			log.Fatal(err)
		}
	}
	ctx := context.Background()
	resp, err := regions[names[0]].Do(ctx, http.MethodPost, "/collections", []byte(`{"name":"`+*collection+`"}`), "application/json")
	if err != nil || (resp.Status != http.StatusCreated && resp.Status != http.StatusConflict) {
		log.Fatalf("create collection: %v HTTP %d %s", err, resp.Status, resp.Body)
	}

	// One marker write per region names the origin node of each region's writes in the feed.
	origins := map[string]string{} // origin hex -> region
	markers := map[string]string{} // marker id -> region
	for _, name := range names {
		id := "marker-" + name + "-" + ulid.Make().String()
		if err := put(ctx, regions[name], *collection, id, "marker"); err != nil {
			log.Fatal(err)
		}
		markers[id] = name
	}
	if err := replbench.ReadFeed(ctx, regions[names[0]], *collection, 30*time.Second, func(e replbench.FeedEvent) bool {
		if region, ok := markers[e.ID]; ok {
			origins[e.Origin] = region
		}
		return len(origins) == len(names)
	}); err != nil {
		log.Fatalf("origins: %v (have %v)", err, origins)
	}
	log.Printf("origins %v", origins)

	out := json.NewEncoder(os.Stdout)
	var docs []*doc
	conv := map[string]*replbench.Latency{}
	for i, a := range names {
		for _, b := range names[i+1:] {
			pair := a + "+" + b
			conv[pair] = replbench.NewLatency()
			for k := range *n {
				d := race(ctx, regions, *collection, pair, a, b, k, *converge)
				if d.Error == "" {
					conv[pair].Record(time.Duration(d.ConvergedMs * float64(time.Millisecond)))
				}
				docs = append(docs, d)
			}
		}
	}

	// Every region's feed must hold both writes of every document; their stamps decide the expected survivor.
	byID := map[string]*doc{}
	for _, d := range docs {
		byID[d.ID] = d
		d.Stamps = map[string]string{}
	}
	stamps := map[string]map[string]replbench.Stamp{}
	feedErrors := map[string]string{}
	// A region's feed holds its own write and the other one only if it applied it: a remote write that arrives after
	// the newer local one loses and never commits there. Coverage counts documents whose feed held both.
	coverage := map[string]int{}
	for _, name := range names {
		got := map[string]map[string]bool{} // this feed's writes per document, by region
		err := replbench.ReadFeed(ctx, regions[name], *collection, 20*time.Second, func(e replbench.FeedEvent) bool {
			d, ok := byID[e.ID]
			region := origins[e.Origin]
			if !ok || region == "" {
				return false
			}
			s, err := replbench.ParseStamp(e)
			if err != nil {
				log.Fatal(err)
			}
			if stamps[e.ID] == nil {
				stamps[e.ID] = map[string]replbench.Stamp{}
				got[e.ID] = map[string]bool{}
			} else if got[e.ID] == nil {
				got[e.ID] = map[string]bool{}
			}
			if prev, seen := stamps[e.ID][region]; seen && prev != s {
				d.Error = fmt.Sprintf("%s: %s's write has two stamps", name, region)
			}
			stamps[e.ID][region] = s
			got[e.ID][region] = true
			d.Stamps[region] = e.Version + "/" + e.Origin
			return !slices.ContainsFunc(docs, func(d *doc) bool { return len(got[d.ID]) < 2 })
		})
		for _, w := range got {
			if len(w) == 2 {
				coverage[name]++
			}
		}
		if err != nil && !errors.Is(err, replbench.ErrFeedIdle) {
			feedErrors[name] = err.Error()
		}
	}
	pass := 0
	for _, d := range docs {
		if d.Error == "" {
			if len(stamps[d.ID]) < 2 {
				d.Error = fmt.Sprintf("feed holds %d of the 2 writes", len(stamps[d.ID]))
			} else {
				d.LWW = replbench.LWWWinner(stamps[d.ID])
				d.OK = d.LWW == d.Survivor
			}
		}
		if d.OK {
			pass++
		}
		_ = out.Encode(d)
	}
	summary := map[string]any{"summary": true, "docs": len(docs), "lww_ok": pass, "feed_errors": feedErrors, "feed_both_writes": coverage}
	for pair, l := range conv {
		summary["converged_"+pair] = l.Summary()
	}
	_ = out.Encode(summary)
	if pass != len(docs) {
		os.Exit(1)
	}
}

func put(ctx context.Context, r *replbench.Region, collection, id, value string) error {
	resp, err := r.Do(ctx, http.MethodPut, replbench.DocPath(collection, id), []byte(`{"w":"`+value+`"}`), "application/json")
	if err != nil {
		return err
	}
	if resp.Status/100 != 2 {
		return fmt.Errorf("PUT %s on %s: HTTP %d %s", id, r.Name, resp.Status, resp.Body)
	}
	return nil
}

// race writes one new document from regions a and b at the same instant and waits until every region agrees on it.
func race(ctx context.Context, regions map[string]*replbench.Region, collection, pair, a, b string, k int, limit time.Duration) *doc {
	d := &doc{Pair: pair, ID: ulid.Make().String(), Status: map[string]int{}, AckMs: map[string]float64{}}
	var mu sync.Mutex
	var wg sync.WaitGroup
	start := make(chan struct{})
	t0 := time.Now()
	var lastAck time.Time
	for _, name := range []string{a, b} {
		wg.Go(func() {
			<-start
			resp, err := regions[name].Do(ctx, http.MethodPut, replbench.DocPath(collection, d.ID), []byte(fmt.Sprintf(`{"w":"%s-%d"}`, name, k)), "application/json")
			at := time.Now()
			mu.Lock()
			defer mu.Unlock()
			d.Status[name] = resp.Status
			d.AckMs[name] = float64(at.Sub(t0).Microseconds()) / 1000
			if err != nil {
				d.Error = err.Error()
			}
			if at.After(lastAck) {
				lastAck = at
			}
		})
	}
	close(start)
	wg.Wait()
	if d.Error != "" || d.Status[a]/100 != 2 || d.Status[b]/100 != 2 {
		if d.Error == "" {
			d.Error = fmt.Sprintf("write status %v", d.Status)
		}
		return d
	}
	deadline := lastAck.Add(limit)
	for {
		values := map[string]string{}
		var poll sync.WaitGroup
		for name, r := range regions {
			poll.Go(func() {
				resp, err := r.Do(ctx, http.MethodGet, replbench.DocPath(collection, d.ID), nil, "")
				var env struct {
					Document struct {
						W string `json:"w"`
					} `json:"document"`
				}
				if err == nil && resp.Status == http.StatusOK && json.Unmarshal(resp.Body, &env) == nil {
					mu.Lock()
					values[name] = env.Document.W
					mu.Unlock()
				}
			})
		}
		poll.Wait()
		agreed := len(values) == len(regions)
		for _, v := range values {
			agreed = agreed && v == values[a]
		}
		if agreed {
			d.ConvergedMs = float64(time.Since(lastAck).Microseconds()) / 1000
			d.Survivor, _, _ = strings.Cut(values[a], "-")
			return d
		}
		if time.Now().After(deadline) {
			d.Error = fmt.Sprintf("not converged within %s: %v", limit, values)
			return d
		}
		time.Sleep(10 * time.Millisecond)
	}
}
