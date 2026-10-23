// repl-local reuses replbench against three separate authenticated test-server processes.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"terrascale.tech/ts-benchmark/internal/replbench"
	"terrascale.tech/ts-benchmark/internal/repllocal"
)

func main() {
	if err := run(); err != nil {
		log.Print(err)
		os.Exit(1)
	}
}

func run() (runErr error) {
	profile := flag.String("profile", "loopback", "loopback (WAN requires isolated network namespaces)")
	docs := flag.Int("docs", 100000, "source documents per write/feed phase")
	repeats := flag.Int("repeats", 3, "independent cluster repetitions")
	scenario := flag.String("scenario", "all", "all, writes, feeds, catchup, rebuild")
	out := flag.String("out", "benchmark/bench/"+time.Now().UTC().Format("2006-01-02")+"-repl-baseline", "output base path without extension")
	flag.Parse()
	if *profile != "loopback" {
		return fmt.Errorf("profile %q unsupported: no host-wide tc/netem changes; use loopback", *profile)
	}
	if *docs < 1 || *repeats < 1 {
		return errors.New("docs and repeats must be positive")
	}
	if !strings.Contains("|all|writes|feeds|catchup|rebuild|", "|"+*scenario+"|") {
		return errors.New("unknown scenario")
	}
	host, err := os.Hostname()
	if err != nil {
		return err
	}
	if os.Getenv("TS_HEAVY_HOST") != host {
		return fmt.Errorf("pin this run with TS_HEAVY_HOST=%s", host)
	}
	root, err := filepath.Abs(".")
	if err != nil {
		return err
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	ctx, deadline := context.WithTimeout(ctx, 12*time.Hour)
	defer deadline()
	r := report{Host: host, Commit: gitValue("rev-parse", "HEAD"), SourceTree: gitValue("rev-parse", "HEAD:src/TerraBase"),
		Profile: *profile, Scenario: *scenario, Docs: *docs, Repeats: *repeats, Started: time.Now().UTC(), HostInfo: hostInfo()}
	defer func() {
		r.Finished = time.Now().UTC()
		if runErr != nil {
			r.Error = runErr.Error()
		}
		runErr = errors.Join(runErr, r.write(*out))
	}()
	for repeat := 1; repeat <= *repeats; repeat++ {
		measure := func(phase func(*repllocal.Cluster) (sample, error)) (phaseErr error) {
			log.Printf("repeat %d/%d: starting fresh three-process cluster", repeat, *repeats)
			c, err := repllocal.StartCluster(ctx, root)
			if err != nil {
				return err
			}
			defer func() {
				if phaseErr != nil {
					c.Preserve = true
					log.Printf("failed phase: private worker logs retained in %s", c.Work)
				}
				phaseErr = errors.Join(phaseErr, c.Close())
			}()
			s, err := phase(c)
			r.Samples = append(r.Samples, s)
			return errors.Join(err, r.write(*out))
		}
		for _, phase := range []struct {
			name           string
			batch, readers int
		}{
			{"single", 1, 0}, {"batch16", 16, 0}, {"feed1", 16, 1}, {"feed10", 16, 10}, {"feed100", 16, 100},
		} {
			if *scenario != "all" && !(*scenario == "writes" && phase.readers == 0) && !(*scenario == "feeds" && phase.readers > 0) {
				continue
			}
			if err := measure(func(c *repllocal.Cluster) (sample, error) {
				return writePhase(ctx, c, repeat, phase.name, *docs, phase.batch, phase.readers)
			}); err != nil {
				return err
			}
		}
		for _, recovery := range []string{"catchup", "rebuild"} {
			if *scenario != "all" && *scenario != recovery {
				continue
			}
			if err := measure(func(c *repllocal.Cluster) (sample, error) {
				return recoveryPhase(ctx, c, repeat, recovery)
			}); err != nil {
				return err
			}
		}
	}
	return nil
}

func gitValue(args ...string) string {
	b, err := exec.Command("git", args...).Output()
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(b))
}

func newRunner(t replbench.Targets, docs, batch int) (*replbench.Runner, error) {
	// One origin isolates receive-side cost on sjc/fra. Runner strides its indexes by region count.
	return replbench.NewRunner(replbench.Config{Docs: docs * len(t.Regions), Batch: batch, Concurrency: 16, Only: "gru",
		Sizes:    []replbench.SizeWeight{{Bytes: 256, Weight: 70}, {Bytes: 1024, Weight: 25}, {Bytes: 8192, Weight: 5}},
		Converge: 30 * time.Minute, Seed: 20261022}, t, log.Printf)
}

func writePhase(ctx context.Context, c *repllocal.Cluster, repeat int, name string, docs, batch, readers int) (s sample, err error) {
	s = sample{Repeat: repeat, Phase: name, Metrics: map[string]float64{}, Before: map[string]repllocal.Snapshot{}, After: map[string]repllocal.Snapshot{}}
	t := c.Targets()
	runner, err := newRunner(t, docs, batch)
	if err != nil {
		return s, err
	}
	if err = runner.Setup(ctx); err != nil {
		return s, err
	}
	stopFeeds, admitted, rejected, err := repllocal.OpenFeeds(ctx, t, readers)
	if err != nil {
		return s, err
	}
	defer func() { err = errors.Join(err, stopFeeds()) }()
	s.Metrics["readers.requested"], s.Metrics["readers.admitted"], s.Metrics["readers.rejected"] = float64(readers), float64(admitted), float64(rejected)
	// Feed cost windows omit visibility probes, whose changes would alter the denominator.
	var stopVisibility func() (map[string]replbench.Summary, error)
	if readers == 0 {
		stopVisibility, err = repllocal.StartVisibility(ctx, t)
		if err != nil {
			return s, err
		}
		defer func() {
			if stopVisibility != nil {
				_, e := stopVisibility()
				err = errors.Join(err, e)
			}
		}()
	}
	beforeWire := c.Bytes()
	for region, w := range c.Nodes {
		s.Before[region], err = repllocal.Scrape(ctx, w.Target.MetricsURL)
		if err != nil {
			return s, err
		}
	}
	if _, err = c.LocalAck(ctx); err != nil {
		return s, err
	}
	stopLoad := startLoadSampler()
	elapsed, err := runner.Load(ctx)
	defer stopLoad()
	if err != nil {
		return s, err
	}
	ack, err := c.LocalAck(ctx)
	if err != nil {
		return s, err
	}
	if ack["local_ack.count"] < float64((docs+batch-1)/batch) || ack["local_ack.capped"] != 0 {
		return s, errors.New("local acknowledgement histogram incomplete or capped")
	}
	if _, ok := ack["local_ack.p99_ms"]; !ok {
		return s, errors.New("local acknowledgement histogram has no percentiles")
	}
	for key, value := range ack {
		s.Metrics[key] = value
	}
	if stopVisibility != nil {
		s.Visibility, err = stopVisibility()
		stopVisibility = nil
		if err != nil {
			return s, err
		}
	}
	if err := runner.AwaitReplication(ctx); err != nil {
		return s, err
	}
	for region, w := range c.Nodes {
		s.After[region], err = repllocal.Scrape(ctx, w.Target.MetricsURL)
		if err != nil {
			return s, err
		}
	}
	s.Load = stopLoad()
	wireBytes := c.Bytes() - beforeWire
	raw := replbench.Result{Name: name, Kind: t.Kind, Config: map[string]any{"docs": docs, "batch": batch, "origin": "gru", "concurrency": 16, "seed": 20261022}, Host: os.Getenv("TS_HEAVY_HOST")}
	raw.Convergence, err = runner.Converge(ctx, time.Now())
	if err != nil {
		return s, err
	}
	runner.Collect(&raw, elapsed.Seconds())
	s.Result = raw
	if !raw.Passed() || raw.Writes["gru"].Created != int64(docs) || raw.Writes["gru"].Failed != 0 || raw.Convergence.Expected != docs {
		return s, errors.New("write load incomplete or exact convergence failed")
	}
	s.Metrics["throughput.docs_per_second"] = raw.Writes["gru"].DocsPerSec
	s.Metrics["remote_ack.p50_ms"], s.Metrics["remote_ack.p99_ms"] = raw.Writes["gru"].Create.P50, raw.Writes["gru"].Create.P99
	s.Metrics["wire.transport_stream_bytes_per_source_doc"] = float64(wireBytes) / float64(docs)
	for region, b := range s.Before {
		a := s.After[region]
		if a.CPUms < b.CPUms || a.Allocated < b.Allocated || a.FeedReads < b.FeedReads || a.MergedGroups < b.MergedGroups {
			return s, fmt.Errorf("%s metric reset", region)
		}
		s.Metrics[region+".cpu_ms_per_source_doc"] = (a.CPUms - b.CPUms) / float64(docs)
		s.Metrics[region+".allocated_bytes_per_source_doc"] = (a.Allocated - b.Allocated) / float64(docs)
		s.Metrics[region+".feed_selects_per_source_doc"] = (a.FeedReads - b.FeedReads) / float64(docs)
		s.Metrics[region+".merged_groups"] = a.MergedGroups - b.MergedGroups
		if groups := a.MergedGroups - b.MergedGroups; groups > 0 {
			s.Metrics[region+".cpu_ms_per_merged_group"] = (a.CPUms - b.CPUms) / groups
			s.Metrics[region+".allocated_bytes_per_merged_group"] = (a.Allocated - b.Allocated) / groups
		}
	}
	for pair, v := range s.Visibility {
		s.Metrics[pair+".visibility_p50_ms"], s.Metrics[pair+".visibility_p99_ms"], s.Metrics[pair+".visibility_max_ms"] = v.P50, v.P99, v.Max
	}
	log.Printf("%s repeat %d: %.1f docs/s; ack p50 %.2f ms p99 %.2f ms", name, repeat, s.Metrics["throughput.docs_per_second"], s.Metrics["remote_ack.p50_ms"], s.Metrics["remote_ack.p99_ms"])
	return s, nil
}
