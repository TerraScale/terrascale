package pitrbench

import (
	"bytes"
	"cmp"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"hash"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"sync"
	"sync/atomic"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type VerifyConfig struct {
	Database string
	Cut      string
	Workers  int

	Versions string

	Audit *Audit

	Shipped []int64

	Compare string
	Label   string

	SkewMs int64

	requireAcked bool
}

type Audit struct {
	Times    []int64
	Problems []string

	Expected []string

	AtMs int64
}

type Partition struct {
	Index       int   `json:"p"`
	Expected    int64 `json:"expected_live"`
	Actual      int64 `json:"actual_live"`
	Missing     int64 `json:"missing,omitempty"`
	Extra       int64 `json:"extra,omitempty"`
	Foreign     int64 `json:"foreign,omitempty"`
	Duplicate   int64 `json:"duplicate,omitempty"`
	OutOfOrder  int64 `json:"out_of_order,omitempty"`
	Content     int64 `json:"content_mismatch,omitempty"`
	Version     int64 `json:"version_mismatch,omitempty"`
	Resurrected int64 `json:"resurrected,omitempty"`

	ResurrectedLate int64  `json:"resurrected_possible_late_attempt,omitempty"`
	ExpectedDigest  string `json:"expected_digest"`
	ActualDigest    string `json:"actual_digest"`
	VersionDigest   string `json:"version_digest"`
	Pages           int    `json:"pages"`
}

type Verification struct {
	Run            string              `json:"run"`
	Database       string              `json:"database"`
	Cut            Cut                 `json:"cut"`
	Versions       string              `json:"versions"`
	Passed         bool                `json:"passed"`
	Totals         Partition           `json:"totals"`
	GlobalDigest   string              `json:"global_digest"`
	ExpectedGlobal string              `json:"expected_global_digest"`
	VersionGlobal  string              `json:"version_global_digest"`
	Examples       map[string][]string `json:"examples,omitempty"`
	Markers        MarkerCheck         `json:"markers"`
	AcctIndex      AcctIndexCheck      `json:"acct_index"`
	Bounded        *BoundedCheck       `json:"bounded,omitempty"`
	A7             A7Check             `json:"a7"`
	Shipped        *ShippedCheck       `json:"shipped,omitempty"`
	Compare        *CompareCheck       `json:"compare,omitempty"`
	AuditProblems  []string            `json:"audit_problems,omitempty"`
	AuditExpected  []string            `json:"audit_expected_problems,omitempty"`
	Seconds        float64             `json:"seconds"`
	DocsPerSec     float64             `json:"docs_per_sec"`
	Partitions     []Partition         `json:"partitions"`
	Problems       []string            `json:"problems,omitempty"`
}

type MarkerCheck struct {
	Want     []string `json:"want"`
	Optional []string `json:"optional,omitempty"`
	Got      []string `json:"got"`
	Passed   bool     `json:"passed"`
}

type AcctIndexCheck struct {
	Values     int      `json:"values_queried"`
	Documents  int64    `json:"documents_matched"`
	Mismatched int      `json:"values_mismatched"`
	Missing    int64    `json:"missing"`
	Extra      int64    `json:"extra"`
	Examples   []string `json:"examples,omitempty"`
}

type ShippedCheck struct {
	Observed          int     `json:"observed_before_audit"`
	MissingFromBucket []int64 `json:"observed_missing_from_bucket,omitempty"`
}

type CompareCheck struct {
	File       string `json:"file"`
	Partitions int    `json:"partitions"`
	Mismatched []int  `json:"mismatched,omitempty"`
}

type BoundedCheck struct {
	Phase           string `json:"phase"`
	Batches         int64  `json:"batches"`
	Present         int64  `json:"present"`
	PartialBatches  int64  `json:"partial_batches"`
	PrefixGaps      int64  `json:"prefix_gaps"`
	PresentRejected int64  `json:"present_but_rejected"`
	MissingRequired int64  `json:"missing_required"`
	IncludeBoundMs  int64  `json:"include_bound_ms,omitempty"`
	LastIncludedAck int64  `json:"last_included_ack_ms"`
	FirstExcluded   int64  `json:"first_excluded_ack_ms,omitempty"`
	PresentDocs     int64  `json:"present_docs"`

	AckedNotRestored int64   `json:"acked_before_cut_not_restored_batches"`
	RPOSeconds       float64 `json:"oldest_acked_not_restored_age_seconds"`
}

type A7Check struct {
	AtMs            int64   `json:"t_ms"`
	SkewMs          int64   `json:"skew_ms"`
	AckedBefore     int64   `json:"acked_before_t_batches"`
	InFlight        int64   `json:"in_flight_batches"`
	InFlightPresent int64   `json:"in_flight_present_batches"`
	SentAfter       int64   `json:"sent_at_or_after_t_batches"`
	Unresolved      int64   `json:"unresolved_batches"`
	MissingAcked    []int64 `json:"acked_before_t_missing,omitempty"`
	PresentLate     []int64 `json:"sent_at_or_after_t_present,omitempty"`
	Conflicts       int64   `json:"ledger_conflicts,omitempty"`
}

const exampleLimit = 20

type verifier struct {
	r        *Run
	cfg      VerifyConfig
	cut      Cut
	oracle   *Oracle
	partial  *Phase
	pmembers []int64
	pslots   int64
	post     []atomic.Uint64
	postN    []atomic.Int32
	preN     []atomic.Int32
	versions map[string]*Versions
	ledgers  map[string]map[int64]*BatchState
	client   *Client
	path     string
	mu       sync.Mutex
	examples map[string][]string
	members  map[string][]int64
}

func Verify(ctx context.Context, r *Run, cfg VerifyConfig) (Verification, error) {
	cut, err := r.Cut(cfg.Cut)
	if err != nil {
		return Verification{}, err
	}
	return verifyAt(ctx, r, cfg, cut)
}

func Reconcile(ctx context.Context, r *Run, workers int) (Verification, error) {
	n, err := LoadNode(r)
	if err != nil {
		return Verification{}, err
	}
	c, err := r.Client(time.Minute)
	if err != nil {
		return Verification{}, err
	}
	cut := Cut{Name: fmt.Sprintf("reconcile-start%d", n.Starts), Kind: "reconcile", AtMs: time.Now().UnixMilli()}
	var lost []string
	for _, p := range r.Phases() {
		if lines, err := LedgerLines(r, p.Name); err != nil || lines == 0 {
			if err != nil {
				return Verification{}, err
			}
			break
		}
		fenced, err := Fenced(ctx, c, r, p.Name)
		if err != nil {
			return Verification{}, err
		}
		if _, err := os.Stat(filepath.Join(r.Dir, "evidence", "fence-"+p.Name+".json")); err == nil && !fenced {
			lost = append(lost, fmt.Sprintf("the fence of %s was proven but does not read back: possible local-durability loss", p.Name))
		}
		if phaseComplete(r, p) != nil {
			cut.Partial = p.Name
			break
		}
		cut.Applied = append(cut.Applied, p.Name)
	}
	out, err := verifyAt(ctx, r, VerifyConfig{Database: r.Database, Workers: workers, Versions: "exact", Label: "reconcile", requireAcked: true}, cut)
	if err != nil {
		return out, err
	}
	if len(lost) > 0 || !out.Passed {
		out.Problems = append(out.Problems, lost...)
		out.Problems = append(out.Problems, "possible local-durability loss: acknowledged state does not read back (never repaired by rewriting)")
		out.Passed = false
		return out, r.Evidence(fmt.Sprintf("verify-%s-%s-reconcile.json", r.Database, cut.Name), out)
	}
	return out, writeJSON(filepath.Join(r.nodeDir(), "reconciled.json"), map[string]any{"starts": n.Starts, "ms": time.Now().UnixMilli(), "cut": cut})
}

func verifyAt(ctx context.Context, r *Run, cfg VerifyConfig, cut Cut) (Verification, error) {
	var err error
	if len(cfg.Shipped) > 0 && cfg.Audit == nil {
		return Verification{}, fmt.Errorf("the shipped-read history is checked against the bucket audit: give both")
	}
	var prev *Verification
	if cfg.Compare != "" {
		b, err := os.ReadFile(cfg.Compare)
		if err != nil {
			return Verification{}, err
		}
		prev = &Verification{}
		if err := json.Unmarshal(b, prev); err != nil {
			return Verification{}, fmt.Errorf("%s: %w", cfg.Compare, err)
		}
		if prev.Database != cfg.Database || prev.Cut.Name != cut.Name || prev.Run != r.ID {
			return Verification{}, fmt.Errorf("%s verified %s at %s of run %s, not %s at %s", cfg.Compare, prev.Database, prev.Cut.Name, prev.Run, cfg.Database, cut.Name)
		}
	}
	v := &verifier{r: r, cfg: cfg, cut: cut, versions: map[string]*Versions{}, ledgers: map[string]map[int64]*BatchState{},
		examples: map[string][]string{}, members: map[string][]int64{},
		path: r.DatabasePath(cfg.Database) + "/collections/" + r.Collection + "/query"}
	if v.oracle, err = NewOracle(r, cut.Applied); err != nil {
		return Verification{}, err
	}
	if cut.Partial != "" {
		p, err := r.Phase(cut.Partial)
		if err != nil {
			return Verification{}, err
		}
		var before []string
		for _, q := range r.Phases() {
			if q.Name == p.Name {
				break
			}
			before = append(before, q.Name)
		}
		if !slices.Equal(before, cut.Applied) {
			return Verification{}, fmt.Errorf("bounded cut %s: applied must be exactly the phases before %s (%v), not %v", cut.Name, p.Name, before, cut.Applied)
		}
		v.partial = &p
		if p.Load() {
			v.pslots = p.hi - p.lo
		} else {
			v.pmembers = p.Members(r.Docs)
			v.pslots = int64(len(v.pmembers))
		}
		v.post = make([]atomic.Uint64, (v.pslots+63)/64)
		v.postN = make([]atomic.Int32, expectedBatches(r, p))
		v.preN = make([]atomic.Int32, expectedBatches(r, p))
	}
	for _, p := range r.Phases() {
		vf, err := OpenVersions(r, p.Name)
		if err != nil {
			return Verification{}, err
		}
		defer vf.Close()
		v.versions[p.Name] = vf
		if v.ledgers[p.Name], err = ReadLedger(r, p.Name); err != nil {
			return Verification{}, err
		}
	}
	if v.client, err = r.Client(5 * time.Minute); err != nil {
		return Verification{}, err
	}
	start := time.Now()
	parts := make([]Partition, r.Partitions)
	next := atomic.Int64{}
	var wg sync.WaitGroup
	errs := make(chan error, max(1, cfg.Workers))
	for range max(1, cfg.Workers) {
		wg.Go(func() {
			for {
				p := int(next.Add(1) - 1)
				if p >= r.Partitions || ctx.Err() != nil {
					return
				}
				res, err := v.partition(ctx, p)
				if err != nil {
					errs <- fmt.Errorf("partition %d: %w", p, err)
					return
				}
				parts[p] = res
				if p%50 == 0 {
					log.Printf("verify %s at %s: partition %d of %d", cfg.Database, cut.Name, p, r.Partitions)
				}
			}
		})
	}
	wg.Wait()
	close(errs)
	for err := range errs {
		return Verification{}, err
	}
	out := Verification{Run: r.ID, Database: cfg.Database, Cut: cut, Versions: cfg.Versions, Partitions: parts, Examples: v.examples}
	g, eg, vg := sha256.New(), sha256.New(), sha256.New()
	t := &out.Totals
	for _, p := range parts {
		t.Expected += p.Expected
		t.Actual += p.Actual
		t.Missing += p.Missing
		t.Extra += p.Extra
		t.Foreign += p.Foreign
		t.Duplicate += p.Duplicate
		t.OutOfOrder += p.OutOfOrder
		t.Content += p.Content
		t.Version += p.Version
		t.Resurrected += p.Resurrected
		t.ResurrectedLate += p.ResurrectedLate
		t.Pages += p.Pages
		g.Write([]byte(p.ActualDigest))
		eg.Write([]byte(p.ExpectedDigest))
		vg.Write([]byte(p.VersionDigest))
	}
	out.GlobalDigest, out.ExpectedGlobal, out.VersionGlobal = hex.EncodeToString(g.Sum(nil)), hex.EncodeToString(eg.Sum(nil)), hex.EncodeToString(vg.Sum(nil))
	if out.Markers, err = v.markers(ctx); err != nil {
		return out, err
	}
	if out.AcctIndex, err = v.acctIndex(ctx); err != nil {
		return out, err
	}
	out.A7 = v.a7()
	if v.partial != nil {
		b, err := v.bounded(&out.A7)
		if err != nil {
			return out, err
		}
		out.Bounded = &b
	}
	if cfg.Audit != nil {
		out.AuditProblems, out.AuditExpected = cfg.Audit.Problems, cfg.Audit.Expected
	}
	if len(cfg.Shipped) > 0 {
		in := map[int64]bool{}
		for _, at := range cfg.Audit.Times {
			in[at] = true
		}
		s := &ShippedCheck{}
		for _, at := range cfg.Shipped {
			if cfg.Audit.AtMs == 0 || at <= cfg.Audit.AtMs {
				s.Observed++
				if !in[at] {
					s.MissingFromBucket = append(s.MissingFromBucket, at)
				}
			}
		}
		out.Shipped = s
	}
	if prev != nil {
		c := &CompareCheck{File: cfg.Compare, Partitions: len(prev.Partitions)}
		for k, p := range parts {
			if k >= len(prev.Partitions) || prev.Partitions[k].VersionDigest != p.VersionDigest {
				c.Mismatched = append(c.Mismatched, k)
			}
		}
		out.Compare = c
	}
	out.Seconds = time.Since(start).Seconds()
	out.DocsPerSec = float64(t.Actual) / max(out.Seconds, 1e-9)
	out.Problems = out.problems()
	out.Passed = len(out.Problems) == 0
	label := cfg.Label
	if label == "" {
		label = time.Now().UTC().Format("20060102T150405")
	}
	return out, r.Evidence(fmt.Sprintf("verify-%s-%s-%s.json", cfg.Database, cut.Name, label), out)
}

func (o Verification) problems() []string {
	var p []string
	t := o.Totals
	if o.Bounded == nil && t.Actual != t.Expected {
		p = append(p, fmt.Sprintf("count %d, expected %d", t.Actual, t.Expected))
	}
	if b := o.Bounded; b != nil && t.Actual != t.Expected+b.PresentDocs {
		p = append(p, fmt.Sprintf("count %d, expected %d plus %d of the partial phase", t.Actual, t.Expected, b.PresentDocs))
	}
	if o.Bounded == nil && o.GlobalDigest != o.ExpectedGlobal {
		p = append(p, "global digest differs from the oracle's")
	}
	for name, n := range map[string]int64{"missing": t.Missing, "extra": t.Extra, "foreign": t.Foreign, "duplicate": t.Duplicate,
		"out of order": t.OutOfOrder, "content mismatch": t.Content, "version mismatch": t.Version, "resurrected": t.Resurrected} {
		if n > 0 {
			p = append(p, fmt.Sprintf("%d %s", n, name))
		}
	}
	if !o.Markers.Passed {
		p = append(p, "markers differ from the cuts")
	}
	if c := o.AcctIndex; c.Values != AcctValues || c.Mismatched > 0 {
		p = append(p, fmt.Sprintf("acct index: %d of %d values queried, %d differ from the oracle (%d missing, %d extra)", c.Values, AcctValues, c.Mismatched, c.Missing, c.Extra))
	}
	if b := o.Bounded; b != nil {
		for name, n := range map[string]int64{"partially applied batches": b.PartialBatches, "per-stream prefix gaps": b.PrefixGaps,
			"required batches missing": b.MissingRequired, "rejected batches present": b.PresentRejected} {
			if n > 0 {
				p = append(p, fmt.Sprintf("%d %s", n, name))
			}
		}
	}
	if a := o.A7; len(a.MissingAcked) > 0 {
		p = append(p, fmt.Sprintf("A-7: %d batches acknowledged before T not wholly present: %v", len(a.MissingAcked), a.MissingAcked[:min(len(a.MissingAcked), exampleLimit)]))
	}
	if a := o.A7; len(a.PresentLate) > 0 {
		p = append(p, fmt.Sprintf("A-7: %d batches first sent at or after T not wholly absent: %v", len(a.PresentLate), a.PresentLate[:min(len(a.PresentLate), exampleLimit)]))
	}
	if a := o.A7; a.Conflicts > 0 {
		p = append(p, fmt.Sprintf("A-7: %d ledger times contradict the cut with skew %d ms (examples a7_conflict): it cannot be judged at T", a.Conflicts, a.SkewMs))
	}
	if s := o.Shipped; s != nil && len(s.MissingFromBucket) > 0 {
		p = append(p, fmt.Sprintf("%d observed shipped reads missing from the bucket (lost segments or snapshots, a generation's final segment included): %v",
			len(s.MissingFromBucket), s.MissingFromBucket[:min(len(s.MissingFromBucket), 10)]))
	}
	if c := o.Compare; c != nil && (len(c.Mismatched) > 0 || c.Partitions != len(o.Partitions)) {
		p = append(p, fmt.Sprintf("%d partition version digests differ from %s", len(c.Mismatched), c.File))
	}
	for _, a := range o.AuditProblems {
		p = append(p, "audit: "+a)
	}
	slices.Sort(p)
	return p
}

func (v *verifier) example(kind string, format string, args ...any) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if len(v.examples[kind]) < exampleLimit {
		v.examples[kind] = append(v.examples[kind], fmt.Sprintf(format, args...))
	}
}

type queryItem struct {
	ID       string          `json:"id"`
	Version  string          `json:"version"`
	Document json.RawMessage `json:"document"`
}

type queryPage struct {
	Items      []queryItem `json:"items"`
	NextCursor *string     `json:"next_cursor"`
}

func (v *verifier) page(ctx context.Context, body map[string]any) (queryPage, error) {
	for attempt := 1; ; attempt++ {
		resp, err := v.client.Do(ctx, http.MethodPost, v.path, mustJSON(body))
		if err == nil && resp.Status == http.StatusOK {
			var page queryPage
			if err := json.Unmarshal(resp.Body, &page); err != nil {
				return page, err
			}
			return page, nil
		}
		if err == nil && resp.Status/100 == 4 && resp.Status != http.StatusTooManyRequests {
			return queryPage{}, fmt.Errorf("query: HTTP %d %s", resp.Status, truncate(resp.Body, 300))
		}
		if ctx.Err() != nil || attempt >= 10 {
			return queryPage{}, fmt.Errorf("query: %v (HTTP %d)", err, resp.Status)
		}
		sleepCtx(ctx, time.Duration(attempt)*500*time.Millisecond)
	}
}

func (v *verifier) partialSlot(i int64) (int64, bool) {
	if v.partial == nil || !v.partial.Contains(i) {
		return 0, false
	}
	if v.partial.Load() {
		return i - v.partial.lo, true
	}
	k, _ := slices.BinarySearch(v.pmembers, i)
	return int64(k), true
}

func (v *verifier) partialIndex(slot int64) int64 {
	if v.partial.Load() {
		return v.partial.lo + slot
	}
	return v.pmembers[slot]
}

func (v *verifier) postState(slot int64) Expected {
	if v.partial.Op == "delete" {
		return Expected{Deleted: true, Phase: v.partial.Name, Slot: slot}
	}
	return Expected{Alive: true, Rev: v.partial.Rev, Phase: v.partial.Name, Slot: slot}
}

func (v *verifier) observe(slot int64, post bool) {
	if post {
		v.post[slot/64].Or(1 << (slot % 64))
		v.postN[slot/BatchSize].Add(1)
	} else {
		v.preN[slot/BatchSize].Add(1)
	}
}

func (v *verifier) alive(i int64) bool {
	if slot, ok := v.partialSlot(i); ok {
		if v.post[slot/64].Load()&(1<<(slot%64)) != 0 {
			return v.postState(slot).Alive
		}
	}
	return v.oracle.At(i).Alive
}

func (v *verifier) partition(ctx context.Context, p int) (Partition, error) {
	r := v.r
	size := (r.Docs + int64(r.Partitions) - 1) / int64(r.Partitions)
	lo, hi := int64(p)*size, min(int64(p+1)*size, r.Docs)
	var bounds []any
	if p > 0 {
		bounds = append(bounds, map[string]any{"gte": []any{"$id", DocID(r.Seed, lo)}})
	}
	if p < r.Partitions-1 {
		bounds = append(bounds, map[string]any{"lt": []any{"$id", DocID(r.Seed, hi)}})
	}
	query := map[string]any{"limit": 1000}
	switch len(bounds) {
	case 1:
		query["filter"] = bounds[0]
	case 2:
		query["filter"] = map[string]any{"and": bounds}
	}
	res := Partition{Index: p}
	exp, act, ver := sha256.New(), sha256.New(), sha256.New()
	nextExpected := lo
	prev := ""
	account := func(upTo int64) {
		for ; nextExpected < upTo; nextExpected++ {
			i := nextExpected
			if slot, ok := v.partialSlot(i); ok {

				pre, post := v.oracle.At(i), v.postState(slot)
				switch {
				case !post.Alive:
					v.observe(slot, true)
				case !pre.Alive:
					v.observe(slot, false)
				default:
					res.Missing++
					v.example("missing", "%d (alive before and after the partial phase %s)", i, v.partial.Name)
				}
				continue
			}
			if e := v.oracle.At(i); e.Alive {
				res.Expected++
				writeDigest(exp, DocID(r.Seed, i), Body(r.Seed, i, e.Rev))
				res.Missing++
				v.example("missing", "%d", i)
			}
		}
	}
	for {
		page, err := v.page(ctx, query)
		if err != nil {
			return res, err
		}
		res.Pages++
		for _, item := range page.Items {
			switch {
			case item.ID == prev:
				res.Duplicate++
				v.example("duplicate", "%s", item.ID)
				continue
			case item.ID < prev:
				res.OutOfOrder++
				v.example("out_of_order", "%s after %s", item.ID, prev)
				continue
			}
			prev = item.ID
			res.Actual++
			canonical, err := benchclient.Canonical(item.Document)
			if err != nil {
				return res, fmt.Errorf("document %s: %w", item.ID, err)
			}
			writeDigest(act, item.ID, canonical)
			ver.Write([]byte(item.ID + "\t" + item.Version + "\n"))
			i, ok := IndexOf(r.Seed, item.ID)
			if !ok || i < lo || i >= hi {
				res.Foreign++
				v.example("foreign", "%s", item.ID)
				continue
			}
			account(i)
			nextExpected = i + 1
			if slot, ok := v.partialSlot(i); ok {

				pre, post := v.oracle.At(i), v.postState(slot)
				var e Expected
				switch {
				case post.Alive && bytes.Equal(canonical, Body(r.Seed, i, post.Rev)):
					v.observe(slot, true)
					e = post
				case pre.Alive && bytes.Equal(canonical, Body(r.Seed, i, pre.Rev)):
					v.observe(slot, false)
					e = pre
				default:
					res.Content++
					v.example("content", "%d: in neither the state before the partial phase %s nor after it: %s", i, v.partial.Name, truncate(canonical, 160))
					continue
				}
				if err := v.checkVersion(&res, i, item.Version, e); err != nil {
					return res, err
				}
				continue
			}
			e := v.oracle.At(i)
			if !e.Alive {
				res.Extra++
				if e.Deleted {
					res.Resurrected++
					if why, late := v.resurrected(i, canonical); late {
						res.ResurrectedLate++
						v.example("resurrected_possible_late_attempt", "%d: %s", i, why)
					} else {
						v.example("resurrected", "%d: %s", i, why)
					}
				} else {
					v.example("extra", "%d", i)
				}
				continue
			}
			res.Expected++
			writeDigest(exp, item.ID, Body(r.Seed, i, e.Rev))
			if !bytes.Equal(canonical, Body(r.Seed, i, e.Rev)) {
				res.Content++
				v.example("content", "%d: %s", i, truncate(canonical, 160))
				continue
			}
			if err := v.checkVersion(&res, i, item.Version, e); err != nil {
				return res, err
			}
		}
		if page.NextCursor == nil || *page.NextCursor == "" {
			break
		}
		query["cursor"] = *page.NextCursor
	}
	account(hi)
	res.ExpectedDigest, res.ActualDigest, res.VersionDigest = hex.EncodeToString(exp.Sum(nil)), hex.EncodeToString(act.Sum(nil)), hex.EncodeToString(ver.Sum(nil))
	if v.partial != nil {

		res.ExpectedDigest = ""
	}
	return res, nil
}

func (v *verifier) checkVersion(res *Partition, i int64, got string, e Expected) error {
	if v.cfg.Versions != "exact" {
		return nil
	}
	want, err := v.versions[e.Phase].Get(e.Slot)
	if err != nil {
		return err
	}
	switch {
	case want == "" && (v.partial == nil || e.Phase != v.partial.Name):
		res.Version++
		v.example("version", "%d: %s, no version recorded by %s slot %d", i, got, e.Phase, e.Slot)
	case want != "" && got != want:
		res.Version++
		v.example("version", "%d: %s, ledger %s", i, got, want)
	}
	return nil
}

func (v *verifier) resurrected(i int64, canonical []byte) (string, bool) {
	for _, p := range v.r.Phases() {
		if p.Op == "delete" || !p.Contains(i) || !bytes.Equal(canonical, Body(v.r.Seed, i, p.Rev)) {
			continue
		}
		slot := i - p.lo
		if !p.Load() {
			v.mu.Lock()
			m, ok := v.members[p.Name]
			if !ok {
				m = p.Members(v.r.Docs)
				v.members[p.Name] = m
			}
			v.mu.Unlock()
			k, _ := slices.BinarySearch(m, i)
			slot = int64(k)
		}
		if st := v.ledgers[p.Name][slot/BatchSize]; st != nil && st.Ambiguous() {
			return fmt.Sprintf("content of %s batch %d, which had an ambiguous attempt at %d: possible late harness attempt", p.Name, slot/BatchSize, st.AmbiguousMs), true
		}
		return fmt.Sprintf("content of %s batch %d, which had no ambiguous attempt", p.Name, slot/BatchSize), false
	}
	return "content no write of this run produces", false
}

func writeDigest(h hash.Hash, id string, canonical []byte) {
	sum := sha256.Sum256(canonical)
	h.Write([]byte(id))
	h.Write([]byte{'\t'})
	h.Write([]byte(hex.EncodeToString(sum[:])))
	h.Write([]byte{'\n'})
}

func (v *verifier) a7() A7Check {
	t, skew := v.cut.AtMs, v.cfg.SkewMs
	a := A7Check{AtMs: t, SkewMs: skew}
	for _, p := range v.r.Phases() {
		if p.Name == v.cut.Partial {
			continue
		}
		applied := slices.Contains(v.cut.Applied, p.Name)
		for b, st := range v.ledgers[p.Name] {
			acked, late := a.classify(st)
			switch {
			case applied && !acked:
				a.Conflicts++
				v.example("a7_conflict", "applied %s batch %d: %s at %d, not acknowledged before T-skew %d", p.Name, b, cmp.Or(st.Terminal, "unresolved"), st.EndMs, t-skew)
			case !applied && !late:
				a.Conflicts++
				v.example("a7_conflict", "%s batch %d (a phase not applied at this cut) first sent at %d, before T+skew %d", p.Name, b, st.FirstMs, t+skew)
			}
		}
	}
	return a
}

func (a *A7Check) classify(st *BatchState) (acked, late bool) {
	acked = (st.Terminal == EvAck || st.Terminal == EvPresent) && st.EndMs < a.AtMs-a.SkewMs
	late = st.FirstMs >= a.AtMs+a.SkewMs
	switch {
	case acked:
		a.AckedBefore++
	case late:
		a.SentAfter++
	default:
		a.InFlight++
	}
	if st.Terminal == "" {
		a.Unresolved++
	}
	return acked, late
}

func (v *verifier) bounded(a *A7Check) (BoundedCheck, error) {
	states := v.ledgers[v.partial.Name]
	b := BoundedCheck{Phase: v.partial.Name, Batches: int64(len(v.postN))}
	var before []int64
	for _, at := range v.cfg.Shipped {
		if at < v.cut.AtMs {
			before = append(before, at)
		}
	}
	slices.Sort(before)
	before = slices.Compact(before)
	if len(before) >= 2 {
		b.IncludeBoundMs = before[len(before)-2]
	}
	type key struct {
		inv    string
		stream int
	}
	type entry struct {
		start   int64
		present bool
	}
	streams := map[key][]entry{}
	for k := range v.postN {
		batch := int64(k)
		full := min(int64(BatchSize), v.pslots-batch*BatchSize)
		post, pre := int64(v.postN[k].Load()), int64(v.preN[k].Load())
		st := states[batch]
		proven := st != nil && (st.Terminal == EvAck || st.Terminal == EvPresent)
		acked, late := false, true
		if st != nil {
			acked, late = a.classify(st)
		}
		switch {
		case acked && post != full:
			a.MissingAcked = append(a.MissingAcked, batch)
		case late && pre != full:
			a.PresentLate = append(a.PresentLate, batch)
		case !acked && !late && post == full:
			a.InFlightPresent++
		}
		switch {
		case post == full:
			b.Present++
			if v.partial.Op != "delete" {
				b.PresentDocs += full
			}
			if proven {
				b.LastIncludedAck = max(b.LastIncludedAck, st.EndMs)
			}
			if st != nil && st.Terminal == EvRejected {
				b.PresentRejected++
			}
		case pre == full:
			for slot := batch * BatchSize; slot < batch*BatchSize+full; slot++ {
				if v.oracle.At(v.partialIndex(slot)).Alive {
					b.PresentDocs++
				}
			}
			if proven && st.EndMs <= v.cut.AtMs {
				b.AckedNotRestored++
				if b.FirstExcluded == 0 || st.EndMs < b.FirstExcluded {
					b.FirstExcluded = st.EndMs
				}
			}
			if proven && (v.cfg.requireAcked || b.IncludeBoundMs > 0 && st.EndMs <= b.IncludeBoundMs) {
				b.MissingRequired++
			}
		default:
			b.PartialBatches++
		}
		if st != nil {

			streams[key{st.Inv, st.Stream}] = append(streams[key{st.Inv, st.Stream}], entry{st.LastStart, post == full})
		}
	}
	for _, es := range streams {
		slices.SortFunc(es, func(a, b entry) int { return cmp.Compare(a.start, b.start) })
		absent := false
		for _, e := range es {
			if !e.present {
				absent = true
			} else if absent {
				b.PrefixGaps++
			}
		}
	}
	if b.FirstExcluded > 0 {
		b.RPOSeconds = float64(v.cut.AtMs-b.FirstExcluded) / 1000
	}
	return b, nil
}

func (v *verifier) markers(ctx context.Context) (MarkerCheck, error) {
	cuts, err := v.r.Cuts()
	if err != nil {
		return MarkerCheck{}, err
	}
	var m MarkerCheck
	for _, c := range cuts {
		sent := c.MarkerMs
		if sent == 0 {

			if _, err := fmt.Sscanf(c.Note, "marker written at %d", &sent); err != nil {
				sent = c.AtMs
			}
		}
		switch {
		case c.Kind == "bounded" || c.Kind == "reconcile":
		case c.AtMs <= v.cut.AtMs:
			m.Want = append(m.Want, c.Name)
		case sent <= v.cut.AtMs:
			m.Optional = append(m.Optional, c.Name)
		}
	}
	path := v.r.DatabasePath(v.cfg.Database) + "/collections/markers/documents?limit=1000"
	resp, err := v.client.JSON(ctx, http.MethodGet, path, nil, nil)
	if err != nil {
		return m, err
	}
	var page queryPage
	if err := json.Unmarshal(resp.Body, &page); err != nil {
		return m, err
	}
	for _, item := range page.Items {
		m.Got = append(m.Got, item.ID)
	}
	slices.Sort(m.Want)
	slices.Sort(m.Got)
	m.Passed = !slices.ContainsFunc(m.Want, func(name string) bool { return !slices.Contains(m.Got, name) }) &&
		!slices.ContainsFunc(m.Got, func(name string) bool { return !slices.Contains(m.Want, name) && !slices.Contains(m.Optional, name) })
	return m, nil
}

func (v *verifier) acctIndex(ctx context.Context) (AcctIndexCheck, error) {
	var c AcctIndexCheck
	var values, docs, mismatched, missing, extra atomic.Int64
	next := atomic.Int64{}
	var wg sync.WaitGroup
	errs := make(chan error, max(1, v.cfg.Workers))
	for range max(1, v.cfg.Workers) {
		wg.Go(func() {
			for {
				a := next.Add(1) - 1
				if a >= AcctValues || ctx.Err() != nil {
					return
				}
				matched, miss, ext, why, err := v.acctValue(ctx, a)
				if err != nil {
					errs <- fmt.Errorf("acct %s: %w", Acct(a), err)
					return
				}
				values.Add(1)
				docs.Add(matched)
				missing.Add(miss)
				extra.Add(ext)
				if miss+ext > 0 {
					mismatched.Add(1)
					v.mu.Lock()
					if len(c.Examples) < exampleLimit {
						c.Examples = append(c.Examples, fmt.Sprintf("%s: %d missing, %d extra (%s)", Acct(a), miss, ext, why))
					}
					v.mu.Unlock()
				}
			}
		})
	}
	wg.Wait()
	close(errs)
	for err := range errs {
		return c, err
	}
	c.Values, c.Documents, c.Mismatched, c.Missing, c.Extra = int(values.Load()), docs.Load(), int(mismatched.Load()), missing.Load(), extra.Load()
	slices.Sort(c.Examples)
	return c, nil
}

func (v *verifier) acctValue(ctx context.Context, a int64) (matched, missing, extra int64, why string, err error) {
	k := a
	expected := func() (string, bool) {
		for ; k < v.r.Docs; k += AcctValues {
			if v.alive(k) {
				id := DocID(v.r.Seed, k)
				k += AcctValues
				return id, true
			}
		}
		return "", false
	}
	want, ok := expected()
	body := map[string]any{"filter": map[string]any{"eq": []any{"/acct", Acct(a)}}, "project": []string{"/acct"}, "limit": 1000}
	for {
		page, err := v.page(ctx, body)
		if err != nil {
			return matched, missing, extra, why, err
		}
		for _, item := range page.Items {
			matched++
			var doc struct {
				Acct string `json:"acct"`
			}
			if json.Unmarshal(item.Document, &doc) != nil || doc.Acct != Acct(a) {
				extra++
				why = fmt.Sprintf("%s carries %s", item.ID, truncate(item.Document, 60))
				continue
			}
			for ok && want < item.ID {
				missing++
				why = "missing " + want
				want, ok = expected()
			}
			if ok && want == item.ID {
				want, ok = expected()
			} else {
				extra++
				why = "extra " + item.ID
			}
		}
		if page.NextCursor == nil || *page.NextCursor == "" {
			break
		}
		body["cursor"] = *page.NextCursor
	}
	for ; ok; want, ok = expected() {
		missing++
		why = "missing " + want
	}
	return matched, missing, extra, why, nil
}
