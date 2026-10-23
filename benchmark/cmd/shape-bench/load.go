package main

import (
	"cmp"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"maps"
	"net"
	"net/http"
	"os"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

// One live-load step on a shared wall clock (seconds from its start): readers open over the ramp and repeat refusals
// until admitBy; the writer starts there; the window opens after warmup; deliveries count through grace; the after
// scrape follows a settle.
const (
	rampS, admitByS, windowS, graceS, settleS = 60, 70, 30, 5, 6
	stepS                                     = 120
)

// nodeStep is one TerraBase node's counters over a step (shard 0 scrapes them).
type nodeStep struct {
	Node     string           `json:"node"`
	Reads    map[string]int64 `json:"storage_reads"` // terrabase.storage.reads delta by path
	Sealed   int64            `json:"sealed"`        // terrabase.shape.seals delta: chunks and snapshot pages published
	Mirrored int64            `json:"mirrored"`      // terrabase.shape.mirror.lag count delta: objects mirrored to Tigris
	Parked   int64            `json:"parked"`        // terrabase.shape.requests.waiting at window start
	CPUPct   float64          `json:"cpu_pct"`       // dotnet.process.cpu.time over the window
	RSSMB    float64          `json:"rss_mb"`        // dotnet.process.memory.working_set at window end
	Raw      [2]string        `json:"raw"`           // metric lines before and after the step
}

// shardStep is one load process's record of one step.
type shardStep struct {
	N, Readers                                                       int
	Admitted, Unresolved, Delivered, Refused, Retries, QoS, Failures int64
	Bytes                                                            int64
	Codes                                                            map[int]int64
	Reasons                                                          map[string]int64 // refusals by status, code and message
	Error                                                            string           `json:",omitempty"`
	Hist                                                             *hdrhistogram.Snapshot
	LoadCPU, LoadRSSMB                                               float64
	Writes                                                           int64      `json:",omitzero"` // shard 0 only
	Nodes                                                            []nodeStep `json:",omitempty"`
}

// load is the V3 live load (README "Live load"): one shard of readers on one shape, and on shard 0 the writer and the
// per-node scrapes. Without -app it runs one shard against the local two-node host and merges the report itself.
func load(args []string) error {
	fs := flag.NewFlagSet("load", flag.ContinueOnError)
	app := fs.String("app", "", "TerraBase Fly app; regions come from regions.<app>.internal (empty: local host)")
	project, database := fs.String("project", "", "project id"), fs.String("database", "", "database id")
	collection := fs.String("collection", "", "collection; shard 0 creates it")
	start := fs.Int64("start", 0, "unix second the first step starts on every shard")
	shard, shards := fs.Int("shard", 0, "this shard"), fs.Int("shards", 1, "load processes")
	steps := fs.String("steps", "1,100,1000,10000", "readers per step, all shards")
	out := fs.String("out", "", "shard record (JSON)")
	originURL := fs.String("origin", "", "a running node's client URL instead of -app (an OCI test cell); its key is TS_BENCH_API_KEY")
	metrics := fs.String("metrics", "", "comma-separated /metrics URLs of the nodes, with -origin")
	if err := fs.Parse(args); err != nil {
		return err
	}
	var ns []int
	for s := range strings.SplitSeq(*steps, ",") {
		n, err := strconv.Atoi(s)
		if err != nil || n < 1 {
			return fmt.Errorf("-steps: %q", s)
		}
		ns = append(ns, n)
	}
	ctx := context.Background()
	reg, token, nodes := cmp.Or(os.Getenv("FLY_REGION"), region), os.Getenv("TS_BENCH_API_KEY"), []string(nil)
	var origin string
	if *originURL != "" {
		origin = *originURL
		if *shard == 0 {
			nodes = strings.Split(*metrics, ",")
		}
	} else if *app == "" {
		work, err := os.MkdirTemp("", "shape-bench-")
		if err != nil {
			return err
		}
		defer os.RemoveAll(work)
		h, err := startHost(ctx, work)
		if err != nil {
			return err
		}
		defer h.stop()
		t := h.targets
		*project, *database, origin, token = t.Project, t.Database, t.Regions[region].URL, t.Regions[region].Token
		*collection, *start = "v3-"+strings.ToLower(ulid.Make().String()), time.Now().Add(10*time.Second).Unix()
		if nodes, _, err = metricsListeners(ctx, t.PID); err != nil {
			return err
		}
	} else {
		origin = "http://" + net.JoinHostPort(reg+"."+*app+".internal", "8080")
		if *shard == 0 {
			var err error
			if nodes, err = flyNodes(*app); err != nil {
				return err
			}
		}
	}
	if *project == "" || *database == "" || *collection == "" || token == "" || *start == 0 {
		return fmt.Errorf("load needs -project, -database, -collection, -start and TS_BENCH_API_KEY")
	}
	t := replbench.Targets{Project: *project, Database: *database, Regions: map[string]replbench.RegionTarget{reg: {URL: origin, Token: token}}}
	w, err := replbench.NewRegion(reg, t, writeRate*4)
	if err != nil {
		return err
	}
	b := &bench{collection: *collection, origin: strings.TrimSuffix(origin, "/"), token: token, writer: w,
		shape: &http.Client{Timeout: 2 * time.Minute, Transport: &http.Transport{MaxIdleConnsPerHost: 10_000}, CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}}}
	b.database = b.origin + "/v1/projects/" + *project + "/databases/" + *database
	if *shard == 0 {
		body, _ := json.Marshal(map[string]string{"name": *collection})
		resp, err := w.Do(ctx, http.MethodPost, "/collections", body, "application/json")
		if err != nil || resp.Status/100 != 2 {
			return fmt.Errorf("create collection: %v HTTP %d %s", err, resp.Status, resp.Body)
		}
	}
	var rec []shardStep
	for i, n := range ns {
		at := time.Unix(*start, 0).Add(time.Duration(i*stepS) * time.Second)
		rec = append(rec, runLoadStep(ctx, b, at, n, *shard, *shards, nodes))
		fmt.Fprintf(os.Stderr, "step N=%d: admitted %d delivered %d %s\n", n, rec[i].Admitted, rec[i].Delivered, rec[i].Error)
	}
	if *app == "" && *originURL == "" {
		return mergeReport(*out, [][]shardStep{rec})
	}
	return replbench.WriteJSON(*out, rec)
}

// flyNodes are the metrics URLs of every machine in every region the app serves, by .internal DNS.
func flyNodes(app string) ([]string, error) {
	txt, err := net.LookupTXT("regions." + app + ".internal")
	if err != nil || len(txt) == 0 {
		return nil, fmt.Errorf("regions.%s.internal: %v", app, err)
	}
	var out []string
	for r := range strings.SplitSeq(strings.Join(txt, ","), ",") {
		ips, err := net.LookupIP(r + "." + app + ".internal")
		if err != nil {
			return nil, err
		}
		for _, ip := range ips {
			out = append(out, "http://"+net.JoinHostPort(ip.String(), "9091")+"/metrics#"+r)
		}
	}
	return out, nil
}

// runLoadStep runs this shard's share of n readers through one step starting at at.
func runLoadStep(ctx context.Context, b *bench, at time.Time, n, shard, shards int, nodes []string) shardStep {
	share := n / shards
	if shard < n%shards {
		share++
	}
	s := shardStep{N: n, Readers: share, Codes: map[int]int64{}, Reasons: map[string]int64{}}
	m := &measure{hist: hdrhistogram.New(1, 120_000_000, 3)}
	writeAt := at.Add(admitByS * time.Second)
	m.start = writeAt.Add(time.Duration(spec.WarmupS) * time.Second)
	m.end = m.start.Add(windowS * time.Second)
	m.deadline = m.end.Add(graceS * time.Second)
	b.admitBy = writeAt
	sleep(ctx, time.Until(at))
	scrapes := [][]string{scrapeAll(ctx, nodes)}
	fctx, stop := context.WithDeadline(ctx, m.deadline)
	defer stop()
	outcomes := make(chan string, share)
	var wg sync.WaitGroup
	for i := range share {
		sleep(ctx, time.Until(at.Add(time.Duration(i)*rampS*time.Second/time.Duration(share))))
		wg.Go(func() { b.followShape(fctx, m, outcomes) })
	}
	var writes, writeQoS atomic.Int64
	written := make(chan error, 1)
	sleep(ctx, time.Until(writeAt))
	if nodes != nil {
		go func() { written <- b.write(fctx, writeAt, m.start, m.end, &writes, &writeQoS) }()
	} else {
		written <- nil
	}
	sleep(ctx, time.Until(m.start))
	scrapes = append(scrapes, scrapeAll(ctx, nodes))
	sampler := replbench.StartLocalSampler(os.Getpid(), nil)
	sleep(ctx, time.Until(m.end))
	scrapes = append(scrapes, scrapeAll(ctx, nodes))
	res := sampler.Stop().Nodes["all nodes (one process)"]
	s.LoadCPU, s.LoadRSSMB = res.CPUCores.Avg*100, res.MemMB.Max
	if err := <-written; err != nil {
		m.fail(fmt.Errorf("writer: %w", err))
	}
	sleep(ctx, time.Until(m.deadline))
	stop()
	wg.Wait()
	sleep(ctx, settleS*time.Second)
	scrapes = append(scrapes, scrapeAll(ctx, nodes))
	// Unresolved readers never answered: neither admitted nor refused past admitBy, nor failed before admission.
	s.Unresolved = int64(share - len(outcomes))
	for range len(outcomes) {
		if <-outcomes == "admitted" {
			s.Admitted++
		}
	}
	m.mu.Lock()
	s.Delivered, s.Hist = m.ok, m.hist.Export()
	m.mu.Unlock()
	s.Refused, s.Retries, s.QoS, s.Failures, s.Bytes, s.Writes = m.refused.Load(), m.retries.Load(), m.qosBusy.Load()+writeQoS.Load(), m.failures.Load(), m.bytes.Load(), writes.Load()
	for code := range m.codes {
		if v := m.codes[code].Load(); v > 0 {
			s.Codes[code] = v
		}
	}
	m.reasons.Range(func(k, v any) bool { s.Reasons[k.(string)] = v.(*atomic.Int64).Load(); return true })
	for i, u := range nodes {
		nd, err := nodeDelta(u, scrapes[0][i], scrapes[1][i], scrapes[2][i], scrapes[3][i])
		if err != nil {
			m.fail(err)
		}
		s.Nodes = append(s.Nodes, nd)
	}
	if err := m.firstErr(); err != nil {
		s.Error = err.Error()
	}
	return s
}

// scrapeAll reads every node's metrics; a failed scrape is an empty body, which nodeDelta reports.
func scrapeAll(ctx context.Context, nodes []string) []string {
	out := make([]string, len(nodes))
	for i, u := range nodes {
		out[i], _ = get(ctx, metricsClient, u)
	}
	return out
}

// nodeDelta is one node's step from its scrapes before the step, at window start and end, and after the settle.
func nodeDelta(node, before, start, end, after string) (nodeStep, error) {
	nd := nodeStep{Node: node, Reads: map[string]int64{}, Raw: [2]string{rawSamples(before), rawSamples(after)}}
	if before == "" || start == "" || end == "" || after == "" {
		return nd, fmt.Errorf("%s: a metrics scrape failed", node)
	}
	r0, err := storageReads(before)
	if err != nil {
		return nd, err
	}
	r1, err := storageReads(after)
	if err != nil {
		return nd, err
	}
	for _, p := range slices.Sorted(maps.Keys(r1)) {
		if r1[p] < r0[p] {
			return nd, fmt.Errorf("%s: %s{path=%q} fell: the node restarted", node, storageReadsMetric, p)
		}
		nd.Reads[p] = int64(r1[p] - r0[p])
	}
	counter := func(body, base string) float64 { v, _ := promValue(body, base, ""); return v }
	nd.Sealed = int64(counter(after, "terrabase_shape_seals") - counter(before, "terrabase_shape_seals"))
	nd.Mirrored = int64(counter(after, "terrabase_shape_mirror_lag_seconds_count") - counter(before, "terrabase_shape_mirror_lag_seconds_count"))
	nd.Parked, _ = shapeWaiting(start)
	nd.CPUPct = (promSum(end, "dotnet_process_cpu_time_seconds") - promSum(start, "dotnet_process_cpu_time_seconds")) / windowS * 100
	nd.RSSMB = counter(end, "dotnet_process_memory_working_set_bytes") / (1 << 20)
	return nd, nil
}

// promSum adds every sample of metric base (all label sets).
func promSum(body, base string) float64 {
	var sum float64
	for _, line := range promLines(body, base) {
		v, _ := promValue(line, base, "")
		sum += v
	}
	return sum
}

// overBound names each node whose shape storage reads (paths shape and shape_body) exceed one per sealed object per
// cache location: the node itself, plus Tigris for each object mirrored there.
func overBound(nodes []nodeStep) []string {
	var out []string
	for _, nd := range nodes {
		if r := nd.Reads["shape"] + nd.Reads["shape_body"]; r > nd.Sealed+nd.Mirrored {
			out = append(out, fmt.Sprintf("%s: %d shape storage reads (shape %d, shape_body %d) over %d sealed objects and %d mirrored: %.2f per object per location, bound 1",
				nd.Node, r, nd.Reads["shape"], nd.Reads["shape_body"], nd.Sealed, nd.Mirrored, float64(r)/float64(max(nd.Sealed+nd.Mirrored, 1))))
		}
	}
	return out
}

// mergeCmd merges shard records: shape-bench merge -out <base> <shard.json>...
func mergeCmd(args []string) error {
	fs := flag.NewFlagSet("merge", flag.ContinueOnError)
	out := fs.String("out", "", "report base path (writes .md and .json)")
	if err := fs.Parse(args); err != nil {
		return err
	}
	var shards [][]shardStep
	for _, f := range fs.Args() {
		var rec []shardStep
		if err := replbench.ReadJSON(f, &rec); err != nil {
			return err
		}
		shards = append(shards, rec)
	}
	return mergeReport(*out, shards)
}

// v3Row is one merged step of the report.
type v3Row struct {
	N                                               int
	Admitted, Unresolved, Writes, Delivered, Missed int64
	Refused, Retries, QoS, Failures, Bytes          int64
	P50MS, P99MS, Ratio304                          float64
	Codes                                           map[int]int64
	Reasons                                         map[string]int64
	LoadCPUPct, LoadRSSMB                           []float64
	Nodes                                           []nodeStep
	Errors, Over                                    []string
}

// mergeReport writes <out>.json and <out>.md from every shard's steps (shard 0 first) and fails when a step breaks the
// bound or a shard recorded an error.
func mergeReport(out string, shards [][]shardStep) error {
	var rows []v3Row
	var failed []string
	for i := range shards[0] {
		r := v3Row{N: shards[0][i].N, Codes: map[int]int64{}, Reasons: map[string]int64{}, Writes: shards[0][i].Writes, Nodes: shards[0][i].Nodes}
		var hist *hdrhistogram.Histogram
		for _, sh := range shards {
			s := sh[i]
			r.Admitted, r.Unresolved, r.Delivered = r.Admitted+s.Admitted, r.Unresolved+s.Unresolved, r.Delivered+s.Delivered
			r.Refused, r.Retries, r.QoS, r.Failures, r.Bytes = r.Refused+s.Refused, r.Retries+s.Retries, r.QoS+s.QoS, r.Failures+s.Failures, r.Bytes+s.Bytes
			for c, v := range s.Codes {
				r.Codes[c] += v
			}
			for k, v := range s.Reasons {
				r.Reasons[k] += v
			}
			r.LoadCPUPct, r.LoadRSSMB = append(r.LoadCPUPct, s.LoadCPU), append(r.LoadRSSMB, s.LoadRSSMB)
			if s.Error != "" {
				r.Errors = append(r.Errors, s.Error)
			}
			if h := hdrhistogram.Import(s.Hist); hist == nil {
				hist = h
			} else {
				hist.Merge(h)
			}
		}
		r.Missed = max(r.Admitted*r.Writes-r.Delivered, 0)
		if hist.TotalCount() > 0 {
			r.P50MS, r.P99MS = float64(hist.ValueAtQuantile(50))/1000, float64(hist.ValueAtQuantile(99))/1000
		}
		if ok := r.Codes[200] + r.Codes[304]; ok > 0 {
			r.Ratio304 = float64(r.Codes[304]) / float64(ok)
		}
		r.Over = overBound(r.Nodes)
		for _, o := range r.Over {
			failed = append(failed, fmt.Sprintf("N=%d %s", r.N, o))
		}
		for _, e := range r.Errors {
			failed = append(failed, fmt.Sprintf("N=%d error: %s", r.N, e))
		}
		rows = append(rows, r)
	}
	if out == "" {
		out = fmt.Sprintf("%s/%s-v3-%s", reportDir, time.Now().UTC().Format(time.DateOnly), ulid.Make())
	}
	if err := replbench.WriteJSON(out+".json", map[string]any{"steps": rows, "violations": failed}); err != nil {
		return err
	}
	if err := os.WriteFile(out+".md", []byte(v3Table(rows)), 0o644); err != nil {
		return err
	}
	fmt.Println(out + ".md")
	if len(failed) > 0 {
		return fmt.Errorf("V3 bound or measurement failed:\n%s", strings.Join(failed, "\n"))
	}
	return nil
}

// v3Table is the report table: one row per step and node.
func v3Table(rows []v3Row) string {
	var b strings.Builder
	b.WriteString("| N | admitted | unresolved | writes | delivered | missed | p50 ms | p99 ms | 304 ratio | statuses | refused / retries / tenant_busy / failures | load CPU % / RSS MB | node | parked | sealed | mirrored | reads shape / shape_body / point | reads per object per location | node CPU % / RSS MB | verdict |\n")
	b.WriteString("|" + strings.Repeat(" --- |", 20) + "\n")
	for _, r := range rows {
		var codes []string
		for _, c := range slices.Sorted(maps.Keys(r.Codes)) {
			codes = append(codes, fmt.Sprintf("%d: %d", c, r.Codes[c]))
		}
		var loadRes []string
		for i := range r.LoadCPUPct {
			loadRes = append(loadRes, fmt.Sprintf("%.0f / %.0f", r.LoadCPUPct[i], r.LoadRSSMB[i]))
		}
		head := fmt.Sprintf("| %d | %d | %d | %d | %d | %d | %.1f | %.1f | %.3f | %s | %d / %d / %d / %d | %s ",
			r.N, r.Admitted, r.Unresolved, r.Writes, r.Delivered, r.Missed, r.P50MS, r.P99MS, r.Ratio304, strings.Join(codes, ", "),
			r.Refused, r.Retries, r.QoS, r.Failures, strings.Join(loadRes, "; "))
		verdict := map[bool]string{true: "within bound", false: "OVER BOUND"}[len(r.Over) == 0]
		for _, nd := range r.Nodes {
			reads := nd.Reads["shape"] + nd.Reads["shape_body"]
			fmt.Fprintf(&b, "%s| %s | %d | %d | %d | %d / %d / %d | %.2f | %.0f / %.0f | %s |\n", head, nd.Node, nd.Parked, nd.Sealed, nd.Mirrored,
				nd.Reads["shape"], nd.Reads["shape_body"], nd.Reads["point"], float64(reads)/float64(max(nd.Sealed+nd.Mirrored, 1)), nd.CPUPct, nd.RSSMB, verdict)
		}
	}
	b.WriteString("\nRefusals by status, code and message:\n\n")
	for _, r := range rows {
		for _, k := range slices.Sorted(maps.Keys(r.Reasons)) {
			fmt.Fprintf(&b, "- N=%d: %d × %s\n", r.N, r.Reasons[k], k)
		}
	}
	return b.String()
}
