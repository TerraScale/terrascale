package replbench

import (
	"encoding/json"
	"fmt"
	"maps"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"time"
)

// RegionWrites is one region's side of the load.
type RegionWrites struct {
	Created    int64   `json:"created"`
	DocsPerSec float64 `json:"docs_per_second"` // created / load wall time
	PerSecond  Series  `json:"per_second"`      // per-second created counts while the region was writing
	Create     Summary `json:"create_latency"`  // one request: a PUT, or a transaction of Batch puts
	Delete     Summary `json:"delete_latency"`
	Patch      Summary `json:"conflict_patch_latency"`
	Throttled  int64   `json:"throttled"`
	Retried    int64   `json:"retried"`
	Failed     int64   `json:"failed"`
	Mismatched int64   `json:"region_mismatches"`
}

// Lag is one region pair's probe lag.
type Lag struct {
	Summary
	Missing int64 `json:"missing"` // probes not seen within two minutes
}

// Result is everything one run recorded; it is written as JSON and as a markdown report.
type Result struct {
	Name        string                  `json:"name"`
	Kind        string                  `json:"kind"`
	Commit      string                  `json:"commit"`
	Host        string                  `json:"host"`
	Start       time.Time               `json:"start"`
	End         time.Time               `json:"end"`
	Regions     []string                `json:"regions"`
	Machines    map[string]string       `json:"machines,omitempty"`
	Config      map[string]any          `json:"config"`
	Collection  string                  `json:"collection"`
	LoadSeconds float64                 `json:"load_seconds"`
	Writes      map[string]RegionWrites `json:"writes"`
	Lag         map[string]Lag          `json:"lag"`
	Convergence Convergence             `json:"convergence"`
	Conflicts   ConflictOutcome         `json:"conflicts"`
	Resources   Resources               `json:"resources"`
	Errors      []string                `json:"errors,omitempty"`
	NotTested   []string                `json:"not_tested"`
}

// Collect fills the load's part of a result from the runner.
func (r *Runner) Collect(res *Result, loadSeconds float64) {
	res.Collection = r.collection
	res.LoadSeconds = loadSeconds
	res.Writes, res.Lag = map[string]RegionWrites{}, map[string]Lag{}
	for _, reg := range r.regions {
		res.Regions = append(res.Regions, reg.Name)
		rates := []float64{}
		if t := r.timeline[reg.Name]; t != nil {
			for _, n := range t.Rates() {
				rates = append(rates, float64(n))
			}
		}
		c := r.counters[reg.Name]
		created := r.created[reg.Name].Load()
		res.Writes[reg.Name] = RegionWrites{
			Created: created, DocsPerSec: float64(created) / max(loadSeconds, 1e-9), PerSecond: seriesOf(rates),
			Create: r.create[reg.Name].Summary(), Delete: r.remove[reg.Name].Summary(), Patch: r.patch[reg.Name].Summary(),
			Throttled: c.Throttled.Load(), Retried: c.Retried.Load(), Failed: c.Failed.Load(), Mismatched: reg.Mismatch.Load(),
		}
	}
	if r.probes != nil {
		for key, l := range r.probes.lag {
			res.Lag[key] = Lag{Summary: l.Summary(), Missing: r.probes.missing[key].Load()}
		}
	}
}

// Passed is the correctness verdict: converged, every region exact, no write of unknown outcome, conflicts identical.
func (res Result) Passed() bool {
	return res.Convergence.Converged && res.Convergence.Unknown == 0 && len(res.Errors) == 0 &&
		res.Conflicts.Identical == res.Conflicts.Docs && res.Conflicts.SameField["invalid"] == 0
}

// Write saves <dir>/<date>-<name>.md and .json and answers the markdown path.
func (res Result) Write(dir string) (string, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	base := filepath.Join(dir, res.Start.UTC().Format("2006-01-02")+"-"+res.Name)
	js, err := json.MarshalIndent(res, "", "  ")
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(base+".json", append(js, '\n'), 0o644); err != nil {
		return "", err
	}
	return base + ".md", os.WriteFile(base+".md", []byte(res.Markdown()), 0o644)
}

// Markdown renders the report: setup, verdict, writes, lag, convergence, conflicts, resources and what was not tested.
func (res Result) Markdown() string {
	var b strings.Builder
	p := func(format string, args ...any) { fmt.Fprintf(&b, format+"\n", args...) }
	verdict := "FAILED"
	if res.Passed() {
		verdict = "PASSED"
	}
	p("# Replication benchmark: %s", res.Name)
	p("")
	p("- Verdict: **%s** (every region holds exactly the expected documents, conflicts identical everywhere)", verdict)
	p("- Kind: %s; regions: %s; host: %s", res.Kind, strings.Join(res.Regions, ", "), res.Host)
	p("- Commit: `%s`; run %s to %s (UTC)", res.Commit, res.Start.UTC().Format(time.DateTime), res.End.UTC().Format(time.DateTime))
	for _, k := range slices.Sorted(maps.Keys(res.Machines)) {
		p("- Machine %s: %s", k, res.Machines[k])
	}
	cfg, _ := json.Marshal(res.Config)
	p("- Config: `%s`", cfg)
	p("- Collection: `%s`", res.Collection)
	for _, e := range res.Errors {
		p("- Error: %s", e)
	}
	p("")
	p("## Writes (load wall time %.1f s)", res.LoadSeconds)
	p("")
	p("| Region | Created | docs/s | per-second avg / max | Request p50 | p95 | p99 | max (ms) | Delete p50 / p99 | Conflict patch p50 / p99 | 429 | Retried | Failed |")
	p("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
	for _, name := range res.Regions {
		w := res.Writes[name]
		p("| %s | %d | %.0f | %.0f / %.0f | %.1f | %.1f | %.1f | %.1f | %.1f / %.1f | %.1f / %.1f | %d | %d | %d |", name, w.Created, w.DocsPerSec, w.PerSecond.Avg, w.PerSecond.Max,
			w.Create.P50, w.Create.P95, w.Create.P99, w.Create.Max, w.Delete.P50, w.Delete.P99, w.Patch.P50, w.Patch.P99, w.Throttled, w.Retried, w.Failed)
	}
	p("")
	p("## Replication lag (probe acknowledged in the origin -> first read that sees it in the target)")
	p("")
	p("| Pair | Probes | p50 | p95 | p99 | max (ms) | Missing |")
	p("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
	for _, key := range slices.Sorted(maps.Keys(res.Lag)) {
		l := res.Lag[key]
		p("| %s | %d | %.1f | %.1f | %.1f | %.1f | %d |", key, l.Count, l.P50, l.P95, l.P99, l.Max, l.Missing)
	}
	c := res.Convergence
	p("")
	p("## Convergence and correctness")
	p("")
	p("- Expected documents: %d (deleted %d, unknown outcome %d); expected digest `%s`", c.Expected, c.Deleted, c.Unknown, c.ExpectedHash)
	p("- End markers visible everywhere: %.1f s after the last acknowledgement", c.MarkersSecs)
	if c.Converged {
		p("- Every region exact: from %.1f s (start of the matching round) to %.1f s (its end) after the last acknowledgement, %d listing round(s)", c.VerifiedSecs, c.VerifiedBySec, c.Rounds)
	} else {
		p("- **Not converged** within the deadline after %d listing round(s)", c.Rounds)
	}
	p("")
	p("| Region | Count | Missing | Extra | Mismatched | Digest | Listing (s) |")
	p("| --- | ---: | ---: | ---: | ---: | --- | ---: |")
	for _, name := range res.Regions {
		rc := c.Regions[name]
		p("| %s | %d | %d | %d | %d | `%.16s…` | %.1f |", name, rc.Count, rc.Missing, rc.Extra, rc.Mismatched, rc.Digest, rc.ListSecs)
		for _, e := range rc.Examples {
			p("|  | %s | | | | | |", e)
		}
	}
	k := res.Conflicts
	p("")
	p("## Conflicts (%d documents, every region overwrites each one concurrently)", k.Docs)
	p("")
	p("- Identical in every region: %d of %d", k.Identical, k.Docs)
	p("- Shared field `s` survivor (a region's last write; `invalid` = no region's last write): %s", tally(k.SameField))
	p("- Own field `f_<region>` (each region's last round): %s", tally(k.OwnField))
	if len(k.Diverged) > 0 {
		p("- Diverged examples: %s", strings.Join(k.Diverged, ", "))
	}
	p("")
	p("## Resources (%s)", res.Resources.Source)
	p("")
	if res.Resources.Note != "" {
		p("%s.", res.Resources.Note)
		p("")
	}
	p("| Node | CPU cores avg / p95 / max | Memory MB avg / p95 / max | Swap MB max | Steal %% avg / max | Disk MB (end) |")
	p("| --- | ---: | ---: | ---: | ---: | ---: |")
	triple := func(s Series, format string) string {
		if s.N == 0 {
			return "n/a"
		}
		return fmt.Sprintf(format+" / "+format+" / "+format, s.Avg, s.P95, s.Max)
	}
	for _, name := range slices.Sorted(maps.Keys(res.Resources.Nodes)) {
		n := res.Resources.Nodes[name]
		swap, steal := "n/a", "n/a"
		if n.SwapMB.N > 0 {
			swap = fmt.Sprintf("%.0f", n.SwapMB.Max)
		}
		if n.StealPct.N > 0 {
			steal = fmt.Sprintf("%.1f / %.1f", n.StealPct.Avg, n.StealPct.Max)
		}
		p("| %s | %s | %s | %s | %s | %.1f |", name, triple(n.CPUCores, "%.2f"), triple(n.MemMB, "%.0f"), swap, steal, n.DiskMB)
	}
	p("")
	p("## Not tested in this run")
	p("")
	for _, t := range res.NotTested {
		p("- %s", t)
	}
	return b.String()
}

func tally(m map[string]int) string {
	if len(m) == 0 {
		return "none"
	}
	var parts []string
	for _, k := range slices.Sorted(maps.Keys(m)) {
		parts = append(parts, fmt.Sprintf("%s %d", k, m[k]))
	}
	return strings.Join(parts, ", ")
}
