// Command xregion-probe measures write-to-visible latency from one TerraBase region to another with a single clock.
//
// It runs on the destination host B: it writes each probe to the origin A over the private mesh and polls its own node
// (loopback) until the probe is served. A write leaves B at t0 and reaches A one way later, so the probe is visible on
// B at latency = t_visible - t0 - RTT/2 after it reached A, where RTT is the median TCP connect time from B to A over the
// same path. The network floor is RTT/2 and overhead = latency - floor. A's own commit time is included in the latency.
// Each line of output is one JSON object; the last one is the run's summary.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

type sample struct {
	ID         string  `json:"id"`
	T0         int64   `json:"t0_unix_ns"` // when the write left B, for matching a packet capture
	AckMs      float64 `json:"ack_ms"`     // t_ack - t0: the origin's write latency seen from B
	VisibleMs  float64 `json:"visible_ms"` // t_visible - t0
	LatencyMs  float64 `json:"latency_ms"` // visible_ms - rtt/2
	OverheadMs float64 `json:"overhead_ms"`
	Polls      int     `json:"polls"`
}

func main() {
	origin := flag.String("origin", "", "origin region's client URL, e.g. http://10.200.109.193:28080")
	local := flag.String("local", "http://127.0.0.1:28080", "this region's client URL")
	label := flag.String("label", "", "direction label, e.g. sjc->fra")
	project := flag.String("project", "", "project id")
	database := flag.String("database", "", "database name")
	collection := flag.String("collection", "", "collection (must exist)")
	keyFile := flag.String("key-file", "", "file holding the API key")
	localKeyFile := flag.String("local-key-file", "", "file holding this region's API key, when it differs (local cells)")
	n := flag.Int("n", 100, "probes")
	every := flag.Duration("every", 200*time.Millisecond, "gap between probes")
	poll := flag.Duration("poll", 2*time.Millisecond, "local poll interval")
	rttSamples := flag.Int("rtt-samples", 21, "TCP connects to the origin for the RTT")
	load := flag.Int("load", 0, "writers that PUT new documents to the origin back to back while the probes run")
	flag.Parse()

	key, err := os.ReadFile(*keyFile)
	if err != nil {
		log.Fatal(err)
	}
	localKey := key
	if *localKeyFile != "" {
		if localKey, err = os.ReadFile(*localKeyFile); err != nil {
			log.Fatal(err)
		}
	}
	t := replbench.Targets{Project: *project, Database: *database, Regions: map[string]replbench.RegionTarget{
		"origin": {URL: *origin, Token: strings.TrimSpace(string(key))},
		"local":  {URL: *local, Token: strings.TrimSpace(string(localKey))},
	}}
	from, err := replbench.NewRegion("origin", t, 4)
	if err != nil {
		log.Fatal(err)
	}
	to, err := replbench.NewRegion("local", t, 4)
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()
	out := json.NewEncoder(os.Stdout)

	rtt := tcpRTT(*origin, *rttSamples)
	floor := rtt / 2
	// Open both clients' connections before the first probe: its write would otherwise also pay a TCP connect to the
	// origin, one more round trip that is the client's and not replication's (in every earlier run, probe 0 was the slowest).
	for _, warm := range []*replbench.Region{from, to} {
		if _, err := warm.Do(ctx, http.MethodGet, replbench.DocPath(*collection, ulid.Make().String()), nil, ""); err != nil {
			log.Fatal(err)
		}
	}
	lat, over, ack := replbench.NewLatency(), replbench.NewLatency(), replbench.NewLatency()
	missing := 0
	// Sustained load beside the probes, on clients of its own so probes never queue behind it.
	loadCtx, stopLoad := context.WithCancel(ctx)
	var loaded atomic.Int64
	var writers sync.WaitGroup
	for range *load {
		loader, err := replbench.NewRegion("origin", t, 1)
		if err != nil {
			log.Fatal(err)
		}
		writers.Go(func() {
			for loadCtx.Err() == nil {
				r, err := loader.Do(loadCtx, http.MethodPut, replbench.DocPath(*collection, ulid.Make().String()), []byte(`{"load":true}`), "application/json")
				if err == nil && r.Status/100 == 2 {
					loaded.Add(1)
				}
			}
		})
	}
	loadStart := time.Now()
	ms := func(d time.Duration) float64 { return float64(d.Microseconds()) / 1000 }
	for i := range *n {
		id := ulid.Make().String()
		t0 := time.Now()
		// The poll runs beside the write: the origin acknowledges only after another region's durable ack, so polling
		// after the ack would measure the ack, not visibility.
		type seen struct {
			at    time.Duration
			polls int
		}
		found := make(chan seen, 1)
		go func() {
			polls := 0
			for time.Since(t0) < time.Minute {
				polls++
				r, err := to.Do(ctx, http.MethodGet, replbench.DocPath(*collection, id), nil, "")
				if err == nil && r.Status == http.StatusOK {
					found <- seen{time.Since(t0), polls}
					return
				}
				time.Sleep(*poll)
			}
			found <- seen{-1, polls}
		}()
		resp, err := from.Do(ctx, http.MethodPut, replbench.DocPath(*collection, id), []byte(`{"probe":`+strconv.Itoa(i)+`}`), "application/json")
		acked := time.Since(t0)
		got := <-found
		if err != nil || resp.Status/100 != 2 || got.at < 0 {
			log.Printf("probe %d: write %v status %d, visible %v", i, err, resp.Status, got.at)
			missing++
			continue
		}
		polls := got.polls
		visible := got.at
		s := sample{ID: id, T0: t0.UnixNano(), AckMs: ms(acked), VisibleMs: ms(visible), LatencyMs: ms(visible - floor), OverheadMs: ms(visible - 2*floor), Polls: polls}
		lat.Record(visible - floor)
		over.Record(visible - 2*floor)
		ack.Record(acked)
		_ = out.Encode(s)
		time.Sleep(*every)
	}
	stopLoad()
	writers.Wait()
	_ = out.Encode(map[string]any{"summary": true, "label": *label, "rtt_tcp_ms": ms(rtt), "floor_ms": ms(floor), "poll_ms": ms(*poll),
		"load_writers": *load, "load_writes_per_s": float64(loaded.Load()) / time.Since(loadStart).Seconds(),
		"latency": lat.Summary(), "overhead": over.Summary(), "origin_ack": ack.Summary(), "missing": missing})
}

// tcpRTT is the median time of n TCP connects to the origin's client port: one SYN, SYN-ACK round trip each.
func tcpRTT(origin string, n int) time.Duration {
	u, err := url.Parse(origin)
	if err != nil {
		log.Fatal(err)
	}
	var d []time.Duration
	for range n {
		t0 := time.Now()
		c, err := net.DialTimeout("tcp", u.Host, 5*time.Second)
		if err != nil {
			log.Fatal(err)
		}
		d = append(d, time.Since(t0))
		_ = c.Close()
		time.Sleep(50 * time.Millisecond)
	}
	slices.Sort(d)
	return d[len(d)/2]
}
