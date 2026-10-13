package pitrbench

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

// WriteConfig is one invocation of a write phase.
//
// Exactly-once rests on server preconditions, not on reads: every op is conditional (create: the document must not be
// live; put and delete: if_match the version the ledger recorded for the previous phase that touched it; the re-create
// put: if_match the tombstone's recorded version), and a transaction is atomic. So at most one attempt of a batch can
// ever apply, however late an earlier timed-out attempt reaches the database, and a retry is the identical request.
// A batch becomes terminal only with proof: a 200 answer (its versions), or a 412 after which every document reads
// back as exactly the state the batch writes, at a version other than its precondition (a tombstone proven by the
// 404's ETag). Anything else stays unresolved in the ledger: never counted as written, failed or absent.
//
// One hazard crosses phases: create only requires the document not to be live (src/TerraBase/Features/Documents/
// ETags.cs), so a load attempt that reached the server after d1 deleted its document would re-create it. Elapsed time
// proves nothing about when a sent request commits, so phases are fenced by the server instead: every transaction
// asserts that fences/closed-<its phase> is absent (assertions run before any write of the transaction and reject it
// whole, src/TerraBase/Features/Documents/DocumentService.cs), and a phase is closed by creating that document only
// once every batch is proven. A late attempt then reaches the database either before the fence, where its
// precondition fails (create needs a non-live document, put and delete a version that no longer exists), or after it,
// where the assertion rejects it. A phase with an unresolved batch can never be fenced, so the next phase and every
// exact cut over it refuse to start.
type WriteConfig struct {
	Phase        string
	Writers      int
	DocsPerSec   float64       // 0: unthrottled
	MaxBatches   int64         // 0: the whole phase; otherwise stop after this many batches reach a terminal event
	MinFreeBytes uint64        // stop when the run directory's filesystem has less free space
	MaxAttempts  int           // per batch per invocation; 0 means 50
	Timeout      time.Duration // per request; 0 means 2 minutes
	// hook, for tests only, is called at named points; an error simulates a crash of this process right there.
	hook func(point string, batch int64) error
	// client, for tests only, replaces the node's client: a fault proxy in front of the node's internal listener.
	client *Client
}

// WriteStats are the counters of one invocation. Attempted counts batches sent at least once; Acked and Present are
// distinct batches (never attempts), so retries cannot inflate them.
type WriteStats struct {
	Phase      string  `json:"phase"`
	Batches    int64   `json:"batches_in_phase"`
	Skipped    int64   `json:"skipped_already_terminal"`
	Attempted  int64   `json:"attempted_batches"`
	Attempts   int64   `json:"attempts"`
	Acked      int64   `json:"acked_batches"`
	Present    int64   `json:"proven_by_read_batches"`
	Unresolved int64   `json:"unresolved_batches"`
	Retries    int64   `json:"retries"`
	Ambiguous  int64   `json:"ambiguous_outcomes"`
	Throttled  int64   `json:"throttled_429"`
	AckedDocs  int64   `json:"terminal_docs"`
	Seconds    float64 `json:"seconds"`
	DocsPerSec float64 `json:"docs_per_sec"`
	P50Ms      float64 `json:"batch_p50_ms"`
	P99Ms      float64 `json:"batch_p99_ms"`
	MaxMs      float64 `json:"batch_max_ms"`
	StoppedFor string  `json:"stopped_for,omitempty"`
	Invocation string  `json:"invocation"`
	Fenced     bool    `json:"fenced"` // the phase was complete and this invocation proved its fence
	FirstAckMs int64   `json:"first_terminal_ms"`
	LastAckMs  int64   `json:"last_terminal_ms"`
}

// errCrash is a simulated crash from a test hook: the writer stops at once, writing nothing more.
var errCrash = errors.New("simulated crash")

type writer struct {
	inv      string // this invocation's id, on every start event
	r        *Run
	cfg      WriteConfig
	phase    Phase
	members  []int64 // mutation phases only
	slots    int64
	ledger   *Ledger
	versions *Versions
	prior    *Oracle              // the phases before this one, for preconditions
	priorV   map[string]*Versions // their version files
	client   *Client
	stats    WriteStats
	hist     *hdrhistogram.Histogram
	histMu   sync.Mutex
	done     atomic.Int64
	terminal atomic.Int64
	ackFirst atomic.Int64
	ackLast  atomic.Int64
}

func (w *writer) index(slot int64) int64 {
	if w.phase.Load() {
		return w.phase.lo + slot
	}
	return w.members[slot]
}

func (w *writer) at(point string, b int64) error {
	if w.cfg.hook == nil {
		return nil
	}
	return w.cfg.hook(point, b)
}

// Write runs (or resumes) a write phase. Every earlier phase must be complete: its versions are this phase's
// preconditions.
func Write(ctx context.Context, r *Run, cfg WriteConfig) (WriteStats, error) {
	phase, err := r.Phase(cfg.Phase)
	if err != nil {
		return WriteStats{}, err
	}
	if cfg.MaxAttempts == 0 {
		cfg.MaxAttempts = 50
	}
	if cfg.Timeout == 0 {
		cfg.Timeout = 2 * time.Minute
	}
	w := &writer{inv: ulid.Make().String(), r: r, cfg: cfg, phase: phase, hist: hdrhistogram.New(1, 600_000, 3), priorV: map[string]*Versions{}}
	w.stats.Invocation = w.inv
	if w.client = cfg.client; w.client == nil {
		if w.client, err = r.Client(cfg.Timeout); err != nil {
			return WriteStats{}, err
		}
	}
	if err := requireReconciled(r); err != nil {
		return WriteStats{}, err
	}
	if err := SingleNode(ctx, w.client); err != nil {
		return WriteStats{}, err
	}
	// Runs set up before phase fences lack the collection; an assertion on a missing collection answers 404.
	if err := created(w.client.JSON(ctx, http.MethodPost, r.DatabasePath(r.Database)+"/collections", map[string]string{"name": "fences"}, nil)); err != nil {
		return WriteStats{}, fmt.Errorf("fences collection: %w", err)
	}
	if fenced, err := Fenced(ctx, w.client, r, phase.Name); err != nil {
		return WriteStats{}, err
	} else if fenced {
		return WriteStats{}, fmt.Errorf("phase %s is fenced (fences/closed-%s exists): a closed phase never runs again", phase.Name, phase.Name)
	}
	var before []string
	for _, p := range r.Phases() {
		if p.Name == phase.Name {
			break
		}
		before = append(before, p.Name)
	}
	if err := RequireFenced(ctx, w.client, r, before); err != nil {
		return WriteStats{}, fmt.Errorf("phase %s: %w", phase.Name, err)
	}
	if phase.Load() {
		w.slots = phase.hi - phase.lo
	} else {
		w.members = phase.Members(r.Docs)
		w.slots = int64(len(w.members))
		for _, name := range before {
			p, _ := r.Phase(name)
			v, err := OpenVersions(r, p.Name)
			if err != nil {
				return WriteStats{}, err
			}
			defer v.Close()
			w.priorV[p.Name] = v
		}
		if w.prior, err = NewOracle(r, before); err != nil {
			return WriteStats{}, err
		}
	}
	states, err := ReadLedger(r, phase.Name)
	if err != nil {
		return WriteStats{}, err
	}
	if w.ledger, err = OpenLedger(r, phase.Name); err != nil {
		return WriteStats{}, err
	}
	defer w.ledger.Close()
	if w.versions, err = OpenVersions(r, phase.Name); err != nil {
		return WriteStats{}, err
	}
	defer w.versions.Close()

	batches := (w.slots + BatchSize - 1) / BatchSize
	w.stats.Phase, w.stats.Batches = phase.Name, batches
	writers := max(1, cfg.Writers)
	parent := ctx
	ctx, stop := context.WithCancel(ctx)
	defer stop()
	var limiter *rateLimiter
	if cfg.DocsPerSec > 0 {
		limiter = newRateLimiter(cfg.DocsPerSec)
	}
	var stopped atomic.Value
	if cfg.MinFreeBytes > 0 {
		go func() {
			for ctx.Err() == nil {
				if free, err := FreeBytes(w.r.Dir); err == nil && free < cfg.MinFreeBytes {
					stopped.Store(fmt.Sprintf("free disk %d < reserve %d", free, cfg.MinFreeBytes))
					stop()
					return
				}
				sleepCtx(ctx, 10*time.Second)
			}
		}()
	}
	start := time.Now()
	go w.progress(ctx, start)
	var wg sync.WaitGroup
	errs := make(chan error, writers)
	for s := range writers {
		wg.Go(func() {
			// Stream s owns batches s, s+W, s+2W, ... and keeps one in flight: per stream, the acknowledged batches are
			// a prefix, which is what an unquiesced cut's restore is checked against.
			for b := int64(s); b < batches; b += int64(writers) {
				if ctx.Err() != nil {
					return
				}
				if st := states[b]; st != nil && st.Terminal != "" {
					atomic.AddInt64(&w.stats.Skipped, 1)
					continue
				}
				first, last := b*BatchSize, min((b+1)*BatchSize, w.slots)
				if limiter != nil {
					limiter.wait(ctx, float64(last-first))
				}
				if err := w.batch(ctx, s, b, first, last, states[b] != nil); err != nil {
					if ctx.Err() == nil || errors.Is(err, errCrash) {
						errs <- fmt.Errorf("stream %d batch %d: %w", s, b, err)
					}
					stop()
					return
				}
				if cfg.MaxBatches > 0 && w.done.Add(1) >= cfg.MaxBatches {
					stopped.Store("max batches")
					stop()
					return
				}
			}
		})
	}
	wg.Wait()
	close(errs)
	w.finish(start)
	if v, ok := stopped.Load().(string); ok {
		w.stats.StoppedFor = v
	}
	var all []error
	if parent.Err() != nil {
		// A signal (the G1 monitor, an operator): the phase is not done, whatever the batches in flight did.
		w.stats.StoppedFor = "interrupted"
		all = append(all, fmt.Errorf("interrupted: %w", parent.Err()))
	}
	for e := range errs {
		all = append(all, e)
	}
	if len(all) == 0 && w.stats.StoppedFor == "" && ctx.Err() == nil && phaseComplete(r, phase) == nil {
		if err := CloseFence(ctx, w.client, r, phase.Name); err != nil {
			all = append(all, err)
		} else {
			w.stats.Fenced = true
		}
	}
	return w.stats, errors.Join(all...)
}

// fenceID is the document of collection fences that closes a phase.
func fenceID(phase string) string { return "closed-" + phase }

// Fenced reports whether a phase's fence exists: a 200 for fences/closed-<phase>. Anything but a 200 or a 404 without
// a version is an error, a deleted fence included.
func Fenced(ctx context.Context, c *Client, r *Run, phase string) (bool, error) {
	resp, err := c.Do(ctx, http.MethodGet, r.DatabasePath(r.Database)+"/collections/fences/documents/by-key?id="+fenceID(phase), nil)
	switch {
	case err != nil:
		return false, fmt.Errorf("fence of %s: %w", phase, err)
	case resp.Status == http.StatusOK:
		return true, nil
	case resp.Status == http.StatusNotFound && resp.ETag == "":
		return false, nil
	}
	return false, fmt.Errorf("fence of %s: HTTP %d (ETag %q) %s", phase, resp.Status, resp.ETag, truncate(resp.Body, 200))
}

// RequireFenced fails unless every phase is complete (every batch proven) and fenced.
func RequireFenced(ctx context.Context, c *Client, r *Run, phases []string) error {
	for _, name := range phases {
		p, err := r.Phase(name)
		if err != nil {
			return err
		}
		if err := phaseComplete(r, p); err != nil {
			return fmt.Errorf("%s is not complete: %w", name, err)
		}
		fenced, err := Fenced(ctx, c, r, name)
		if err != nil {
			return err
		}
		if !fenced {
			return fmt.Errorf("%s is complete but not fenced: rerun its write to close it", name)
		}
	}
	return nil
}

// CloseFence closes a complete phase: a conditional create of fences/closed-<phase>, proven by its 200 or by a 412
// followed by a read of the fence. An ambiguous attempt (no answer, 5xx) may have applied, so it is retried as the
// identical conditional create, which applies at most once: the retry's 412 plus the read proves it. Anything else
// leaves the phase open (rerun to prove it).
func CloseFence(ctx context.Context, c *Client, r *Run, phase string) error {
	p, err := r.Phase(phase)
	if err != nil {
		return err
	}
	if err := phaseComplete(r, p); err != nil {
		return fmt.Errorf("phase %s cannot be fenced: %w", phase, err)
	}
	if err := requireReconciled(r); err != nil {
		return fmt.Errorf("phase %s cannot be fenced: %w", phase, err)
	}
	if err := SingleNode(ctx, c); err != nil {
		return err
	}
	body := mustJSON(map[string]any{"ops": []any{map[string]any{"op": "create", "collection": "fences", "id": fenceID(phase),
		"document": map[string]any{"phase": phase, "closedMs": time.Now().UnixMilli()}}}})
	how, ambiguous := "created", []string{}
	for attempt := 1; ; attempt++ {
		resp, err := c.Do(ctx, http.MethodPost, r.DatabasePath(r.Database)+"/transactions", body)
		switch {
		case err == nil && resp.Status == http.StatusOK:
		case err == nil && resp.Status == http.StatusPreconditionFailed:
			if fenced, err := Fenced(ctx, c, r, phase); err != nil || !fenced {
				return fmt.Errorf("fence of %s: 412, yet it does not read back (%v)", phase, err)
			}
			how = "existed (412, read back)"
		case ctx.Err() == nil && attempt < 10 && (err != nil || resp.Status/100 == 5):
			note := fmt.Sprint(err)
			if err == nil {
				note = fmt.Sprintf("HTTP %d %s", resp.Status, truncate(resp.Body, 200))
			}
			ambiguous = append(ambiguous, note)
			log.Printf("%s: fence attempt %d ambiguous (%s); resending the identical conditional create", phase, attempt, note)
			sleepCtx(ctx, time.Duration(attempt)*250*time.Millisecond)
			continue
		case err != nil:
			return fmt.Errorf("fence of %s: %w (unproven: rerun)", phase, err)
		default:
			return fmt.Errorf("fence of %s: HTTP %d %s (unproven: rerun)", phase, resp.Status, truncate(resp.Body, 200))
		}
		log.Printf("%s: fenced (%s, attempt %d)", phase, how, attempt)
		return r.Evidence("fence-"+phase+".json", map[string]any{"phase": phase, "proven": how, "attempts": attempt,
			"ambiguous_attempts": ambiguous, "ms": time.Now().UnixMilli()})
	}
}

// phaseComplete reports an error, naming the unresolved batches, unless every batch of p has a terminal, proven outcome.
func phaseComplete(r *Run, p Phase) error {
	states, err := ReadLedger(r, p.Name)
	if err != nil {
		return err
	}
	var unresolved []int64
	done := int64(0)
	for b, st := range states {
		if st.Terminal != EvAck && st.Terminal != EvPresent {
			unresolved = append(unresolved, b)
			continue
		}
		done++
	}
	slices.Sort(unresolved)
	want := expectedBatches(r, p)
	switch {
	case len(unresolved) > 0:
		shown := unresolved[:min(len(unresolved), 20)]
		return fmt.Errorf("%s has %d unresolved batches %v (no proven outcome; they block its fence, later phases and cuts over it), %d proven, %d never sent",
			p.Name, len(unresolved), shown, done, want-done-int64(len(unresolved)))
	case done != want:
		return fmt.Errorf("%s has %d of %d batches proven, %d never sent", p.Name, done, want, want-done)
	}
	return nil
}

// batch sends one batch until it has a proven outcome. A cancelled context leaves the outcome unknown in the ledger;
// the next invocation resends the identical, conditional request. sentBefore: an earlier invocation sent it.
func (w *writer) batch(ctx context.Context, stream int, b, first, last int64, sentBefore bool) error {
	// ambiguous: an earlier attempt may have applied (or still apply), so no rejection of a later one proves anything.
	ambiguous := sentBefore
	body, priors, err := w.body(first, last)
	if err != nil {
		return err
	}
	path := w.r.DatabasePath(w.r.Database) + "/transactions"
	for attempt := 1; ; attempt++ {
		if attempt > w.cfg.MaxAttempts {
			atomic.AddInt64(&w.stats.Unresolved, 1)
			_ = w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: EvUnresolved, Ms: time.Now().UnixMilli(), Note: "no proven outcome"})
			return fmt.Errorf("no proven outcome after %d attempts", w.cfg.MaxAttempts)
		}
		atomic.AddInt64(&w.stats.Attempts, 1)
		if attempt == 1 {
			atomic.AddInt64(&w.stats.Attempted, 1)
		}
		if err := w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Inv: w.inv, Stream: stream, Attempt: attempt, Ev: EvStart, Ms: time.Now().UnixMilli()}); err != nil {
			return err
		}
		if err := w.at("after-intent", b); err != nil {
			return err
		}
		t0 := time.Now()
		resp, err := w.client.Do(ctx, http.MethodPost, path, body)
		rx := time.Now().UnixMilli() // an acknowledgement's time is its receipt, not the later ledger write
		w.record(time.Since(t0))
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if err := w.at("after-response", b); err != nil {
			return err
		}
		switch {
		case err == nil && resp.Status == http.StatusOK:
			versions, offset, err := w.parse(resp.Body, first, last)
			if err != nil {
				return err
			}
			return w.finishBatch(stream, b, attempt, EvAck, resp.Status, offset, first, versions, rx)
		case err == nil && resp.Status == http.StatusTooManyRequests:
			atomic.AddInt64(&w.stats.Throttled, 1)
			sleepCtx(ctx, max(resp.RetryAfter, 200*time.Millisecond))
		case err == nil && (resp.Status == http.StatusPreconditionFailed || resp.Status == http.StatusNotFound):
			// A precondition no longer holds. Only proof that this batch's exact writes are in place ends it.
			versions, why, err := w.prove(ctx, first, last, priors)
			if err != nil {
				return err
			}
			if versions == nil {
				atomic.AddInt64(&w.stats.Unresolved, 1)
				note := fmt.Sprintf("HTTP %d %s; %s", resp.Status, truncate(resp.Body, 200), why)
				_ = w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: EvUnresolved, Ms: time.Now().UnixMilli(), Status: resp.Status, Note: note})
				return fmt.Errorf("unresolved: %s", note)
			}
			return w.finishBatch(stream, b, attempt, EvPresent, resp.Status, "", first, versions, time.Now().UnixMilli())
		case err == nil && resp.Status/100 == 4 && ambiguous:
			// This attempt applied nothing, but an earlier one may have: unresolved, never a terminal rejection.
			atomic.AddInt64(&w.stats.Unresolved, 1)
			note := fmt.Sprintf("HTTP %d after an ambiguous attempt: %s", resp.Status, truncate(resp.Body, 200))
			_ = w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: EvUnresolved, Ms: time.Now().UnixMilli(), Status: resp.Status, Note: note})
			return fmt.Errorf("unresolved: %s", note)
		case err == nil && resp.Status/100 == 4:
			_ = w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: EvRejected, Ms: time.Now().UnixMilli(), Status: resp.Status, Note: truncate(resp.Body, 300)})
			return fmt.Errorf("rejected: HTTP %d %s", resp.Status, truncate(resp.Body, 300))
		default:
			// Transport error, timeout or 5xx: it may apply now, later or never. The retry is the same conditional
			// request, so whichever attempt applies first is the only one that can.
			atomic.AddInt64(&w.stats.Ambiguous, 1)
			ambiguous = true
			note := fmt.Sprint(err)
			if err == nil {
				note = fmt.Sprintf("HTTP %d %s", resp.Status, truncate(resp.Body, 200))
			}
			if err := w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: EvRetry, Ms: time.Now().UnixMilli(), Status: resp.Status, Note: note}); err != nil {
				return err
			}
			sleepCtx(ctx, time.Duration(min(attempt, 8))*250*time.Millisecond)
		}
	}
}

// finishBatch records versions, fsyncs them, then appends the terminal ledger event at time at: a crash between the two
// leaves the batch unknown, and the resent request proves it again.
func (w *writer) finishBatch(stream int, b int64, attempt int, ev string, status int, offset string, first int64, versions []string, at int64) error {
	if err := w.versions.Put(first, versions); err != nil {
		return err
	}
	if err := w.versions.Sync(); err != nil {
		return err
	}
	if err := w.at("after-versions", b); err != nil {
		return err
	}
	if err := w.ledger.Append(Event{Phase: w.phase.Name, Batch: b, Stream: stream, Attempt: attempt, Ev: ev, Ms: at, Status: status, Offset: offset}); err != nil {
		return err
	}
	if ev == EvAck {
		atomic.AddInt64(&w.stats.Acked, 1)
	} else {
		atomic.AddInt64(&w.stats.Present, 1)
	}
	w.terminal.Add(int64(len(versions)))
	w.ackFirst.CompareAndSwap(0, at)
	w.ackLast.Store(at)
	return nil
}

// body is the conditional transaction of slots [first, last) and each op's precondition version ("" for a create).
func (w *writer) body(first, last int64) ([]byte, []string, error) {
	var sb strings.Builder
	sb.Grow(int(last-first) * 560)
	sb.WriteString(`{"ops":[`)
	priors := make([]string, 0, last-first)
	for slot := first; slot < last; slot++ {
		if slot > first {
			sb.WriteByte(',')
		}
		i := w.index(slot)
		op, prior := w.phase.Op, ""
		if !w.phase.Load() {
			e := w.prior.At(i)
			if e.Phase == "" {
				return nil, nil, fmt.Errorf("document %d has no earlier phase to condition on", i)
			}
			v, err := w.priorV[e.Phase].Get(e.Slot)
			if err != nil || v == "" {
				return nil, nil, fmt.Errorf("document %d: no recorded %s version to condition on (%v)", i, e.Phase, err)
			}
			prior = v
			if e.Deleted && op == "create" {
				op = "put" // re-create exactly over the recorded tombstone: a missed delete fails, it cannot be hidden
			}
		}
		priors = append(priors, prior)
		sb.WriteString(`{"op":"` + op + `","collection":"` + w.r.Collection + `","id":"` + DocID(w.r.Seed, i) + `"`)
		if prior != "" {
			sb.WriteString(`,"if_match":"` + prior + `"`)
		}
		if op != "delete" {
			sb.WriteString(`,"document":`)
			sb.Write(Body(w.r.Seed, i, w.phase.Rev))
		}
		sb.WriteByte('}')
	}
	// The phase fence: once fences/closed-<phase> exists, no attempt of this phase applies anything.
	sb.WriteString(`,{"op":"assert","collection":"fences","id":"` + fenceID(w.phase.Name) + `","absent":true}]}`)
	return []byte(sb.String()), priors, nil
}

// parse checks a transaction answer op by op and returns its versions in op order.
func (w *writer) parse(body []byte, first, last int64) ([]string, string, error) {
	var resp struct {
		Offset  string `json:"offset"`
		Results []struct {
			ID       string `json:"id"`
			Version  string `json:"version"`
			Asserted bool   `json:"asserted"`
		} `json:"results"`
	}
	if err := json.Unmarshal(body, &resp); err != nil {
		return nil, "", fmt.Errorf("transaction answer: %w", err)
	}
	if int64(len(resp.Results)) != last-first+1 {
		return nil, "", fmt.Errorf("transaction answered %d results for %d ops and the fence assertion", len(resp.Results), last-first)
	}
	// An assertion answers {"asserted":true} (src/TerraBase/Features/Documents/TransactionEndpoint.cs).
	if f := resp.Results[last-first]; !f.Asserted || f.ID != "" || f.Version != "" {
		return nil, "", fmt.Errorf("the fence assertion answered %+v, want asserted", f)
	}
	versions := make([]string, last-first)
	for k, res := range resp.Results[:last-first] {
		if want := DocID(w.r.Seed, w.index(first+int64(k))); res.ID != want || res.Version == "" {
			return nil, "", fmt.Errorf("result %d is id %q version %q, want id %q", k, res.ID, res.Version, want)
		}
		versions[k] = res.Version
	}
	return versions, resp.Offset, nil
}

// prove reads every document of a batch after a failed precondition. It returns their versions only when each is
// exactly what the batch writes (the body at its revision, or a tombstone shown by a 404 carrying a version) at a
// version other than the op's precondition; otherwise nil and why. Mixed results are an atomicity violation.
func (w *writer) prove(ctx context.Context, first, last int64, priors []string) ([]string, string, error) {
	versions := make([]string, 0, last-first)
	written := 0
	why := ""
	for slot := first; slot < last; slot++ {
		i := w.index(slot)
		resp, err := w.get(ctx, i)
		if err != nil {
			return nil, "", err
		}
		ok, version, reason := w.written(i, resp, priors[slot-first])
		if ok {
			written++
		} else if why == "" {
			why = fmt.Sprintf("document %d: %s", i, reason)
		}
		versions = append(versions, version)
	}
	switch {
	case written == len(versions):
		return versions, "", nil
	case written == 0:
		return nil, "none of the batch's writes are in place: " + why, nil
	default:
		return nil, fmt.Sprintf("atomicity violation: %d of %d writes in place; %s", written, len(versions), why), nil
	}
}

// written reports whether a read shows document i exactly as this phase writes it.
func (w *writer) written(i int64, resp Response, prior string) (bool, string, string) {
	if w.phase.Op == "delete" {
		switch {
		case resp.Status != http.StatusNotFound:
			return false, "", fmt.Sprintf("HTTP %d, want a tombstone", resp.Status)
		case resp.ETag == "":
			return false, "", "404 without a tombstone version: absent is not proof of this delete"
		case resp.ETag == prior:
			return false, "", "tombstone version equals the precondition"
		}
		return true, resp.ETag, ""
	}
	if resp.Status != http.StatusOK {
		return false, "", fmt.Sprintf("HTTP %d", resp.Status)
	}
	var doc struct {
		Version  string          `json:"version"`
		Document json.RawMessage `json:"document"`
	}
	if err := json.Unmarshal(resp.Body, &doc); err != nil {
		return false, "", err.Error()
	}
	canonical, err := replbench.Canonical(doc.Document)
	if err != nil {
		return false, "", err.Error()
	}
	switch {
	case !bytes.Equal(canonical, Body(w.r.Seed, i, w.phase.Rev)):
		return false, "", "content is not this phase's body"
	case doc.Version == "" || doc.Version == prior:
		return false, "", "version equals the precondition"
	}
	return true, doc.Version, ""
}

func (w *writer) get(ctx context.Context, i int64) (Response, error) {
	path := w.r.DatabasePath(w.r.Database) + "/collections/" + w.r.Collection + "/documents/by-key?id=" + url.QueryEscape(DocID(w.r.Seed, i))
	for attempt := 1; ; attempt++ {
		resp, err := w.client.Do(ctx, http.MethodGet, path, nil)
		if err == nil && (resp.Status == http.StatusOK || resp.Status == http.StatusNotFound) {
			return resp, nil
		}
		if ctx.Err() != nil || attempt >= 20 {
			return resp, fmt.Errorf("read %d: %v (HTTP %d)", i, err, resp.Status)
		}
		sleepCtx(ctx, time.Duration(attempt)*200*time.Millisecond)
	}
}

func (w *writer) record(d time.Duration) {
	w.histMu.Lock()
	_ = w.hist.RecordValue(max(1, d.Milliseconds()))
	w.histMu.Unlock()
}

func (w *writer) finish(start time.Time) {
	w.stats.Seconds = time.Since(start).Seconds()
	w.stats.AckedDocs = w.terminal.Load()
	w.stats.Retries = w.stats.Attempts - w.stats.Attempted
	w.stats.DocsPerSec = float64(w.stats.AckedDocs) / max(w.stats.Seconds, 1e-9)
	w.stats.P50Ms = float64(w.hist.ValueAtQuantile(50))
	w.stats.P99Ms = float64(w.hist.ValueAtQuantile(99))
	w.stats.MaxMs = float64(w.hist.Max())
	w.stats.FirstAckMs, w.stats.LastAckMs = w.ackFirst.Load(), w.ackLast.Load()
}

func (w *writer) progress(ctx context.Context, start time.Time) {
	last, lastAt := int64(0), time.Now()
	for ctx.Err() == nil {
		sleepCtx(ctx, 10*time.Second)
		if ctx.Err() != nil {
			return
		}
		n, now := w.terminal.Load(), time.Now()
		lag := "?"
		if f, ok, err := w.client.BackupStatus(ctx, w.r.DatabaseID); err == nil && ok {
			lag = fmt.Sprintf("%.1fs pending=%d", f.LagSeconds, f.PendingBytes)
		}
		log.Printf("%s: %d docs proven (%.0f/s now, %.0f/s avg), backup lag %s", w.phase.Name, n,
			float64(n-last)/now.Sub(lastAt).Seconds(), float64(n)/now.Sub(start).Seconds(), lag)
		last, lastAt = n, now
	}
}

// rateLimiter paces documents per second across all streams.
type rateLimiter struct {
	mu    sync.Mutex
	rate  float64
	start time.Time
	sent  float64
}

func newRateLimiter(rate float64) *rateLimiter { return &rateLimiter{rate: rate, start: time.Now()} }

func (l *rateLimiter) wait(ctx context.Context, docs float64) {
	l.mu.Lock()
	l.sent += docs
	due := l.start.Add(time.Duration(l.sent / l.rate * float64(time.Second)))
	l.mu.Unlock()
	sleepCtx(ctx, time.Until(due))
}

func sleepCtx(ctx context.Context, d time.Duration) {
	if d <= 0 {
		return
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
}

// FreeBytes is the free space of the filesystem holding path.
func FreeBytes(path string) (uint64, error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return 0, err
	}
	return st.Bavail * uint64(st.Bsize), nil
}
