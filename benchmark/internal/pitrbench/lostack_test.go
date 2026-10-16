package pitrbench

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

// dropProxy is a test-only fault proxy between the writer and a node's internal listener (h2c). Each armed match fires
// once, on the first request whose body contains it: the request is forwarded, the node answers (so the transaction
// commits), the proxy keeps the answer, closes the client's connection and sends nothing back. Every POST
// /transactions exchange is recorded with the node's answer, so a test can count the node's commits per batch.
type dropProxy struct {
	target string
	rt     http.RoundTripper
	mu     sync.Mutex
	arms   []*dropArm
	log    []exchange
}

type dropArm struct {
	label string
	match []byte
	fired bool
}

type exchange struct {
	Ms      int64
	Body    []byte
	Status  int
	Answer  []byte
	Dropped string // the arm that discarded the node's answer
}

type connKey struct{}

func startDropProxy(t *testing.T, nodePort int) (*dropProxy, int) {
	t.Helper()
	var p http.Protocols
	p.SetUnencryptedHTTP2(true)
	dp := &dropProxy{target: "http://127.0.0.1:" + strconv.Itoa(nodePort), rt: &http.Transport{Protocols: &p}}
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	srv := &http.Server{Handler: dp, Protocols: &p,
		ConnContext: func(ctx context.Context, c net.Conn) context.Context { return context.WithValue(ctx, connKey{}, c) }}
	go srv.Serve(l)
	t.Cleanup(func() { srv.Close() })
	return dp, l.Addr().(*net.TCPAddr).Port
}

func (p *dropProxy) arm(label, match string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.arms = append(p.arms, &dropArm{label: label, match: []byte(match)})
}

func (p *dropProxy) exchanges() []exchange {
	p.mu.Lock()
	defer p.mu.Unlock()
	return slices.Clone(p.log)
}

func (p *dropProxy) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	body, err := io.ReadAll(req.Body)
	if err != nil {
		panic(http.ErrAbortHandler)
	}
	// The forwarded request outlives the client: a dropped answer still commits.
	out, err := http.NewRequestWithContext(context.WithoutCancel(req.Context()), req.Method, p.target+req.URL.RequestURI(), bytes.NewReader(body))
	if err != nil {
		panic(err)
	}
	out.Header = req.Header.Clone()
	resp, err := p.rt.RoundTrip(out)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	answer, err := io.ReadAll(resp.Body)
	resp.Body.Close()
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	p.mu.Lock()
	var fired *dropArm
	for _, a := range p.arms {
		if !a.fired && bytes.Contains(body, a.match) {
			a.fired, fired = true, a
			break
		}
	}
	if req.Method == http.MethodPost && strings.HasSuffix(req.URL.Path, "/transactions") {
		e := exchange{Ms: time.Now().UnixMilli(), Body: body, Status: resp.StatusCode, Answer: answer}
		if fired != nil {
			e.Dropped = fired.label
		}
		p.log = append(p.log, e)
	}
	p.mu.Unlock()
	if fired != nil {
		req.Context().Value(connKey{}).(net.Conn).Close()
		panic(http.ErrAbortHandler)
	}
	for _, h := range []string{"Content-Type", "ETag", "Retry-After"} {
		if v := resp.Header.Get(h); v != "" {
			w.Header().Set(h, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	_, _ = w.Write(answer)
}

// TestRealBinaryLostAcknowledgements runs a real terrabase binary (opt-in, see the skip message) with the drop proxy in
// front of its internal listener: the committed answer of one load-a batch, of load-b's phase fence, and of the u1, d1
// and r1 batches is discarded. Each lost answer must be an ambiguous attempt in the ledger whose identical retry gets a
// 412 or 404, and proof by read must make the batch present with exactly the versions the node returned when it
// committed (or prove the fence); the node must commit every batch exactly once; and after each phase a fenced exact
// cut (the clock-free quiesce on the node's real backup) and an exact verify of the source pass. Evidence:
// evidence/lostack-<phase>.json in the run directory.
func TestRealBinaryLostAcknowledgements(t *testing.T) {
	dir := os.Getenv("PITR_REAL_RUN")
	if dir == "" {
		t.Skip("opt-in: PITR_REAL_RUN (new run dir), PITR_REAL_BINARY, PITR_REAL_SHA256, PITR_REAL_COMMIT, PITR_REAL_ENV (0600 env file of a local s3-serve)")
	}
	binary, commit, env := os.Getenv("PITR_REAL_BINARY"), os.Getenv("PITR_REAL_COMMIT"), os.Getenv("PITR_REAL_ENV")
	sum, err := FileSHA256(binary)
	if err != nil || sum != os.Getenv("PITR_REAL_SHA256") || commit == "" || env == "" {
		t.Fatalf("binary %s sha256 %s (%v), want PITR_REAL_SHA256; PITR_REAL_COMMIT and PITR_REAL_ENV are required", binary, sum, err)
	}
	if _, err := os.Stat(dir); err == nil {
		t.Fatalf("%s exists: a run directory is new", dir)
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	r := &Run{Dir: dir, ID: filepath.Base(dir), Seed: 20261012, Docs: 20_000, LoadSplit: 10_000, Partitions: 10, Commit: commit,
		Binary: binary, BinarySHA256: sum, EnvFile: env, Environment: "Production", Target: "local-s3-emulator", Created: time.Now().UTC()}
	if err := r.Save(); err != nil {
		t.Fatal(err)
	}
	ctx := t.Context()
	if _, err := StartNode(ctx, r); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := StopNode(r, 2*time.Minute); err != nil {
			t.Error(err)
		}
	})
	if err := Setup(ctx, r); err != nil {
		t.Fatal(err)
	}
	steps, err := Probe(ctx, r)
	if err != nil {
		t.Fatal(err)
	}
	_ = r.Evidence("probe.json", steps)
	for _, s := range steps {
		if !s.Passed {
			t.Fatalf("probe %s: %s (skipped %v)", s.Name, s.Detail, s.Skipped)
		}
	}
	c, err := r.Client(30 * time.Second)
	if err != nil {
		t.Fatal(err)
	}
	for deadline := time.Now().Add(time.Minute); ; time.Sleep(time.Second) {
		if _, ok, err := c.BackupStatus(ctx, r.DatabaseID); err == nil && ok {
			break
		} else if time.Now().After(deadline) {
			t.Fatalf("backup does not list the database after a minute (%v)", err)
		}
	}
	cutAndVerify := func(name string, applied ...string) {
		t.Helper()
		cut, err := TakeCut(ctx, r, name, applied, "", false, 10*time.Minute)
		if err != nil {
			t.Fatalf("cut %s: %v", name, err)
		}
		if cut.ShippedV0 == cut.ShippedV1 || cut.ShippedV1 == cut.ShippedMs || cut.WindowMs <= cut.AtMs {
			t.Fatalf("cut %s quiesce: %+v", name, cut)
		}
		out, err := Verify(ctx, r, VerifyConfig{Database: r.Database, Cut: name, Workers: 4, Versions: "exact", Label: "source"})
		if err != nil || !out.Passed {
			t.Fatalf("exact verify at %s: %v %v", name, err, out.Problems)
		}
	}
	cutAndVerify("C0")
	cases := []struct{ phase, label, match string }{
		{"load-a", "batch 3", batchMatch(r, "load-a", 3)},
		{"load-b", "phase fence", `"id":"closed-load-b","op":"create"`},
		{"u1", "batch 0", batchMatch(r, "u1", 0)},
		{"d1", "batch 0", batchMatch(r, "d1", 0)},
		{"r1", "batch 0", batchMatch(r, "r1", 0)},
	}
	var applied []string
	for k, tc := range cases {
		n, err := LoadNode(r)
		if err != nil {
			t.Fatal(err)
		}
		proxy, port := startDropProxy(t, n.InternalPort)
		proxy.arm(tc.label, tc.match)
		stats, err := Write(ctx, r, WriteConfig{Phase: tc.phase, Writers: 4, client: NewClient(port, strings.Repeat("a", 64), r.Owner, 2*time.Minute)})
		_ = r.Evidence("write-"+tc.phase+".json", stats)
		// A dropped fence answer is the fence's own ambiguous attempt (fence evidence), not a batch's.
		if err != nil || !stats.Fenced || stats.Unresolved != 0 || (stats.Ambiguous < 1 && tc.label != "phase fence") {
			t.Fatalf("%s: %v, stats %+v", tc.phase, err, stats)
		}
		checkLostAck(t, r, proxy, tc.phase, tc.label, stats)
		if k == 0 {
			// The A-7 probe's T: the moment the harness found batch 3's answer lost. The batch committed before it and was
			// proven after it, so it was in flight at T.
			states, err := ReadLedger(r, tc.phase)
			if err != nil || states[3] == nil || states[3].AmbiguousMs == 0 {
				t.Fatalf("load-a batch 3 ledger %+v: %v", states[3], err)
			}
			if err := r.AddCut(Cut{Name: "A7lost", Kind: "bounded", Partial: tc.phase, AtMs: states[3].AmbiguousMs,
				Note: "A-7 probe: load-a batch 3's answer was dropped by the proxy at this time"}); err != nil {
				t.Fatal(err)
			}
		}
		applied = append(applied, tc.phase)
		cutAndVerify(fmt.Sprintf("L%d", k+1), applied...)
	}
	// The last cut through the product's restore (Path A), verified logically: the proven batches are restored.
	rc, err := r.Client(time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	last := fmt.Sprintf("L%d", len(cases))
	cut, _ := r.Cut(last)
	if res, err := Restore(ctx, r, rc, last, cut.AtMs, "lostack-restored", "source"); err != nil || res.Outcome != OutcomeRestored {
		t.Fatalf("restore at %s: %s %v", last, res.Outcome, err)
	}
	if out, err := Verify(ctx, r, VerifyConfig{Database: "lostack-restored", Cut: last, Workers: 4, Versions: "logical", Label: "path-a"}); err != nil || !out.Passed {
		t.Fatalf("restore at %s: %v %v", last, err, out.Problems)
	}
	// A-7 around the dropped answers (Path A): the outcome is recorded whatever it is; a 201 must hold every batch whole
	// (the dropped ones in flight at T, either way) and no unresolved batch. Its full A-7 verdict is evidence only here:
	// on the pinned binary the before-fix control (pitr-run.sh a7) is where a violation is expected and judged.
	probe, _ := r.Cut("A7lost")
	res, err := Restore(ctx, r, rc, probe.Name, probe.AtMs, "lostack-a7", "source")
	if err != nil {
		t.Fatalf("restore at A7lost: %s %v", res.Outcome, err)
	}
	verdict := map[string]any{"t_ms": probe.AtMs, "outcome": res.Outcome}
	if res.Outcome == OutcomeRestored {
		out, err := Verify(ctx, r, VerifyConfig{Database: "lostack-a7", Cut: probe.Name, Workers: 4, Versions: "logical", Label: "path-a"})
		if err != nil {
			t.Fatal(err)
		}
		if b := out.Bounded; b.PartialBatches != 0 || out.A7.Unresolved != 0 || slices.Contains(out.A7.MissingAcked, 3) || slices.Contains(out.A7.PresentLate, 3) {
			t.Fatalf("A7lost: bounded %+v a7 %+v: a batch not whole, an unresolved batch, or batch 3 not in flight", b, out.A7)
		}
		verdict["a7"], verdict["passed"], verdict["problems"] = out.A7, out.Passed, out.Problems
	}
	t.Logf("A-7 probe around the dropped answers: %v", verdict)
	if err := r.Evidence("a7-lostack.json", verdict); err != nil {
		t.Fatal(err)
	}
}

// batchMatch is a request-body substring unique to batch b of a phase: its first document's id.
func batchMatch(r *Run, phase string, b int64) string {
	p, _ := r.Phase(phase)
	return `"id":"` + DocID(r.Seed, p.Members(r.Docs)[b*BatchSize]) + `"`
}

// checkLostAck checks one phase against the proxy's record of what the node answered: the armed answer was dropped;
// the node committed every batch exactly once (one 200 per batch body); every batch is terminal and its recorded
// versions are exactly the ones the node returned at that commit; every batch proven by read had an ambiguous attempt
// whose retries got 412 or 404; and a dropped fence answer was retried and proven by read.
func checkLostAck(t *testing.T, r *Run, proxy *dropProxy, phase, label string, stats WriteStats) {
	t.Helper()
	p, _ := r.Phase(phase)
	members := p.Members(r.Docs)
	states, err := ReadLedger(r, phase)
	if err != nil {
		t.Fatal(err)
	}
	v, err := OpenVersions(r, phase)
	if err != nil {
		t.Fatal(err)
	}
	defer v.Close()
	type batchRecord struct {
		Terminal       string   `json:"terminal"`
		Attempts       int      `json:"attempts"`
		NodeCommits    int      `json:"node_commits"`
		RetryStatuses  []int    `json:"retry_statuses,omitempty"`
		Dropped        string   `json:"dropped_answer,omitempty"`
		VersionsMatch  bool     `json:"versions_match_the_commit"`
		LedgerEvents   []string `json:"ledger_events,omitempty"`
		FirstVersion   string   `json:"first_version"`
		CommittedFirst string   `json:"committed_first_version"`
	}
	batches := map[int64]*batchRecord{}
	var fence []string
	for _, e := range proxy.exchanges() {
		var tx struct {
			Ops []struct {
				Collection string `json:"collection"`
				ID         string `json:"id"`
			} `json:"ops"`
		}
		if err := json.Unmarshal(e.Body, &tx); err != nil || len(tx.Ops) == 0 {
			t.Fatalf("unparsable transaction body: %v", err)
		}
		if tx.Ops[0].Collection == "fences" {
			fence = append(fence, fmt.Sprintf("%d%s", e.Status, map[bool]string{true: " (answer dropped)"}[e.Dropped != ""]))
			continue
		}
		i, ok := IndexOf(r.Seed, tx.Ops[0].ID)
		if !ok || !p.Contains(i) {
			t.Fatalf("a %s transaction starts with %s, not a document of the phase", phase, tx.Ops[0].ID)
		}
		k, _ := slices.BinarySearch(members, i)
		b := int64(k) / BatchSize
		rec := batches[b]
		if rec == nil {
			rec = &batchRecord{}
			batches[b] = rec
		}
		if e.Dropped != "" {
			rec.Dropped = e.Dropped
		}
		if e.Status != http.StatusOK {
			rec.RetryStatuses = append(rec.RetryStatuses, e.Status)
			continue
		}
		rec.NodeCommits++
		var answer struct {
			Results []struct {
				Version string `json:"version"`
			} `json:"results"`
		}
		if err := json.Unmarshal(e.Answer, &answer); err != nil {
			t.Fatal(err)
		}
		first, last := b*BatchSize, min((b+1)*BatchSize, int64(len(members)))
		if int64(len(answer.Results)) != last-first+1 {
			t.Fatalf("%s batch %d: the node answered %d results", phase, b, len(answer.Results))
		}
		rec.VersionsMatch = true
		for slot := first; slot < last; slot++ {
			got, _ := v.Get(slot)
			if got != answer.Results[slot-first].Version {
				rec.VersionsMatch = false
			}
		}
		rec.CommittedFirst = answer.Results[0].Version
		rec.FirstVersion, _ = v.Get(first)
	}
	ledgerLines, _ := os.ReadFile(filepath.Join(r.Dir, "ledger", phase+".jsonl"))
	want := expectedBatches(r, p)
	if int64(len(states)) != want || int64(len(batches)) != want {
		t.Fatalf("%s: %d batches in the ledger, %d seen by the proxy, want %d", phase, len(states), len(batches), want)
	}
	present := 0
	for b, st := range states {
		rec := batches[b]
		rec.Terminal, rec.Attempts = st.Terminal, st.Attempts
		if rec.Dropped != "" || st.Terminal == EvPresent {
			for line := range strings.Lines(string(ledgerLines)) {
				var e Event
				if json.Unmarshal([]byte(line), &e) == nil && e.Batch == b {
					rec.LedgerEvents = append(rec.LedgerEvents, fmt.Sprintf("a%d %s %d %s", e.Attempt, e.Ev, e.Status, truncate([]byte(e.Note), 80)))
				}
			}
		}
		switch {
		case rec.NodeCommits != 1:
			t.Fatalf("%s batch %d: the node committed %d attempts, want exactly 1", phase, b, rec.NodeCommits)
		case !rec.VersionsMatch:
			t.Fatalf("%s batch %d: recorded versions differ from the node's commit (%s vs %s)", phase, b, rec.FirstVersion, rec.CommittedFirst)
		case st.Terminal == EvPresent && (st.Retries < 1 || len(rec.RetryStatuses) == 0 ||
			slices.ContainsFunc(rec.RetryStatuses, func(s int) bool { return s != 412 && s != 404 })):
			t.Fatalf("%s batch %d proven by read without an ambiguous attempt and 412/404 retries: %+v", phase, b, rec)
		case st.Terminal != EvAck && st.Terminal != EvPresent:
			t.Fatalf("%s batch %d: terminal %q", phase, b, st.Terminal)
		case rec.Dropped != "" && st.Terminal != EvPresent:
			t.Fatalf("%s batch %d: its answer was dropped, yet the batch is %s", phase, b, st.Terminal)
		}
		if st.Terminal == EvPresent {
			present++
		}
	}
	if label == "phase fence" {
		var ev map[string]any
		if err := readJSON(filepath.Join(r.Dir, "evidence", "fence-"+phase+".json"), &ev); err != nil {
			t.Fatal(err)
		}
		if !slices.Equal(fence, []string{"200 (answer dropped)", "412"}) || !strings.HasPrefix(fmt.Sprint(ev["proven"]), "existed") {
			t.Fatalf("%s fence: node answers %v, evidence %v; want the dropped commit, then a 412 proven by read", phase, fence, ev)
		}
	} else if present == 0 {
		t.Fatalf("%s: no batch was proven by read", phase)
	}
	fired := false
	for _, a := range proxy.arms {
		fired = fired || a.fired
	}
	if !fired {
		t.Fatalf("%s: the %s arm never fired", phase, label)
	}
	shown := map[int64]*batchRecord{}
	for b, rec := range batches {
		if rec.Dropped != "" || rec.Terminal == EvPresent {
			shown[b] = rec
		}
	}
	_ = r.Evidence("lostack-"+phase+".json", map[string]any{
		"phase": phase, "armed": label, "binary_sha256": r.BinarySHA256, "commit": r.Commit, "batches": want, "proven_by_read": present,
		"acked": stats.Acked, "ambiguous_attempts": stats.Ambiguous, "every_batch_committed_once_at_node": true,
		"recorded_versions_equal_node_commit": true, "fence_answers": fence, "batches_with_lost_answers": shown,
	})
	t.Logf("%s (%s dropped): %d batches, %d proven by read, %d acked, %d ambiguous attempts, fence answers %v", phase, label, want, present,
		stats.Acked, stats.Ambiguous, fence)
}
