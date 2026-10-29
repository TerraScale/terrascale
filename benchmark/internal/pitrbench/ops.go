package pitrbench

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"time"

	"github.com/oklog/ulid/v2"
)

// Setup creates the run's tenant on its node as the operator: an organization on the enterprise plan (no QoS limits,
// 30-day backup retention, so nothing of the run is trimmed or throttled), a project, the database, the collection and
// an index on acct (so restores also prove index rebuild). IDs are this run's own ULIDs, recorded in run.json.
func Setup(ctx context.Context, r *Run) error {
	if r.Owner == "" {
		r.Owner = ulid.Make().String()
		r.Database, r.Collection = "pitr", "events"
		if err := r.Save(); err != nil {
			return err
		}
	}
	c, err := r.Client(time.Minute)
	if err != nil {
		return err
	}
	return createTenant(ctx, c, r)
}

func Adopt(ctx context.Context, r *Run) error {
	c, err := r.Client(time.Minute)
	if err != nil {
		return err
	}
	if r.Organization == "" || r.Project == "" || r.DatabaseID == "" {
		return fmt.Errorf("the run has no registered source database")
	}
	var database struct {
		ID string `json:"id"`
	}
	if _, err := c.JSON(ctx, http.MethodGet, r.ManagementPath(), nil, &database); err != nil {
		return fmt.Errorf("recover the source catalog before adopting this run: %w", err)
	}
	if database.ID != r.DatabaseID {
		return fmt.Errorf("the recovered database does not match the run")
	}
	return createCollections(ctx, c, r)
}

func createTenant(ctx context.Context, c *Client, r *Run) error {
	create := func(path, name string) (string, error) {
		var resource struct {
			ID string `json:"id"`
		}
		_, err := c.JSON(ctx, http.MethodPost, path, map[string]string{"name": name}, &resource)
		return resource.ID, err
	}
	var err error
	if r.Organization == "" {
		r.Organization, err = create("/v1/management/organizations", "pitr-"+r.ID)
		if err != nil {
			return fmt.Errorf("organization: %w", err)
		}
		if err := r.Save(); err != nil {
			return err
		}
	}
	root := "/v1/management/organizations/" + r.Organization
	if _, err := c.JSON(ctx, http.MethodPut, root+"/plan", map[string]string{"plan": "enterprise"}, nil); err != nil {
		return fmt.Errorf("plan: %w", err)
	}
	if r.Project == "" {
		r.Project, err = create(root+"/projects", "pitr")
		if err != nil {
			return fmt.Errorf("project: %w", err)
		}
		if err := r.Save(); err != nil {
			return err
		}
	}
	if r.DatabaseID == "" {
		r.DatabaseID, err = create(root+"/projects/"+r.Project+"/databases", r.Database)
		if err != nil {
			return fmt.Errorf("database: %w", err)
		}
		if err := r.Save(); err != nil {
			return err
		}
	}
	return createCollections(ctx, c, r)
}

func createCollections(ctx context.Context, c *Client, r *Run) error {
	db := r.DatabasePath(r.Database)
	// Never drop events, markers or fences: the oracle, the cuts and the phase fences rest on them.
	for _, name := range []string{r.Collection, "markers", "fences", "nudges"} {
		if err := created(c.JSON(ctx, http.MethodPost, db+"/collections", map[string]string{"name": name}, nil)); err != nil {
			return fmt.Errorf("collection %s: %w", name, err)
		}
	}
	index := map[string]any{"collection": r.Collection, "fields": []string{"acct"}, "name": "by_acct"}
	if err := created(c.JSON(ctx, http.MethodPost, r.ManagementPath()+"/indexes", index, nil)); err != nil {
		return fmt.Errorf("index: %w", err)
	}
	return nil
}

// created accepts a create that a previous setup of this run already made (409 name_taken or index_exists): the run's
// database is its own, so the existing collection or index can only be this run's.
func created(resp Response, err error) error {
	if err != nil && resp.Status == http.StatusConflict && (bytes.Contains(resp.Body, []byte(`"name_taken"`)) || bytes.Contains(resp.Body, []byte(`"index_exists"`))) {
		return nil
	}
	return err
}

// TakeCut records a restore point. A bounded cut is taken while partial is being written and records only the time.
//
// An exact cut requires quiescence, and enforces it: every applied phase is complete and fenced (the fence comes first,
// then the marker), no other phase has a ledger line, and no ledger changes while the cut is taken (any change means a
// writer ran). One second after the last acknowledgement it creates a marker document (collection markers, id = cut
// name) and proves it shipped without comparing clocks: LastShippedAt changes only when a backup pass finishes, and a
// pass may have begun before the marker committed. V0 is the value at the first poll after the marker PUT returned; a
// nudge (collection nudges, never verified) gives the shipper data; V1 != V0 is a pass that ended after that poll;
// another nudge; V2 != V1 with nothing pending or lagging is a later pass, which began after the V1 pass ended, so
// after the marker committed: it read the marker and every applied batch. The cut then records the next whole second
// (AtMs: a time with no fraction for the product's 1 s commit times to round) and holds until 1.1 s past it, so
// nothing written later can fall at or before it. Whatever a restore at AtMs answers is recorded, never assumed. Last, the restore window must reach AtMs: it ends at
// LastShippedAt, which empty passes never advance, so a post-cut nudge (committed after AtMs+1.1 s, never inside a
// restore at AtMs) must change it. Every wait is bounded by wait and fails on timeout.
//
// An unshipped cut (a node without backup) is an exact cut without the backup waits: it verifies the source against
// the oracle and is NOT a PITR cut; Restore refuses it.
func TakeCut(ctx context.Context, r *Run, name string, applied []string, partial string, unshipped bool, wait time.Duration) (Cut, error) {
	if _, err := r.Cut(name); err == nil {
		return Cut{}, fmt.Errorf("cut %q exists", name)
	}
	c, err := r.Client(30 * time.Second)
	if err != nil {
		return Cut{}, err
	}
	cut := Cut{Name: name, Kind: "exact", Applied: applied}
	if unshipped {
		if partial != "" {
			return Cut{}, fmt.Errorf("an unshipped cut is quiesced: it takes no partial phase")
		}
		cut.Kind = "unshipped"
	}
	if partial != "" {
		cut.Kind, cut.Partial = "bounded", partial
		cut.AtMs = time.Now().UnixMilli()
		return cut, r.AddCut(cut)
	}
	if err := requireReconciled(r); err != nil {
		return Cut{}, err
	}
	if err := SingleNode(ctx, c); err != nil {
		return Cut{}, err
	}
	isApplied := map[string]bool{}
	for _, name := range applied {
		isApplied[name] = true
	}
	cut.LedgerLines = map[string]int{}
	for _, p := range r.Phases() {
		n, err := LedgerLines(r, p.Name)
		if err != nil {
			return Cut{}, err
		}
		if n > 0 && !isApplied[p.Name] {
			return Cut{}, fmt.Errorf("phase %s has %d ledger lines but is not applied: an exact cut needs every written phase applied", p.Name, n)
		}
		cut.LedgerLines[p.Name] = n
	}
	if err := RequireFenced(ctx, c, r, applied); err != nil {
		return Cut{}, fmt.Errorf("cut %s: %w", name, err)
	}
	for _, name := range applied {
		states, err := ReadLedger(r, name)
		if err != nil {
			return Cut{}, err
		}
		for _, st := range states {
			cut.LastAckMs = max(cut.LastAckMs, st.EndMs)
		}
	}
	db := r.DatabasePath(r.Database)
	deadline := time.Now().Add(wait)
	sleepCtx(ctx, time.Until(time.UnixMilli(cut.LastAckMs+1000)))
	marker := time.Now().UnixMilli()
	// A conditional create: no attempt can rewrite the marker after the cut. A 412 is accepted only when the marker
	// reads back (an earlier, unrecorded attempt of this cut wrote it before now).
	resp, err := c.Do(ctx, http.MethodPost, db+"/transactions", mustJSON(map[string]any{"ops": []any{map[string]any{
		"op": "create", "collection": "markers", "id": name, "document": map[string]any{"cut": name, "writtenMs": marker}}}}))
	switch {
	case err != nil:
		return Cut{}, fmt.Errorf("marker: %w", err)
	case resp.Status == http.StatusPreconditionFailed:
		if got, err := c.Do(ctx, http.MethodGet, db+"/collections/markers/documents/by-key?id="+name, nil); err != nil || got.Status != http.StatusOK {
			return Cut{}, fmt.Errorf("marker: 412, yet it does not read back (%v)", err)
		}
	case resp.Status != http.StatusOK:
		return Cut{}, fmt.Errorf("marker: HTTP %d %s", resp.Status, truncate(resp.Body, 200))
	}
	cut.Note, cut.MarkerMs = fmt.Sprintf("marker written at %d", marker), marker
	if !unshipped {
		if err := created(c.JSON(ctx, http.MethodPost, db+"/collections", map[string]string{"name": "nudges"}, nil)); err != nil {
			return Cut{}, fmt.Errorf("nudges collection: %w", err)
		}
		v0, err := lastShipped(ctx, c, r, deadline)
		if err != nil {
			return Cut{}, err
		}
		if err := nudge(ctx, c, r, name, "after-marker"); err != nil {
			return Cut{}, err
		}
		f, err := waitShipped(ctx, c, r, deadline, "finish a pass after the marker", func(f BackupFile) bool { return changed(f, v0) })
		if err != nil {
			return Cut{}, err
		}
		v1 := *f.LastShippedAt
		if err := nudge(ctx, c, r, name, "pass"); err != nil {
			return Cut{}, err
		}
		f, err = waitShipped(ctx, c, r, deadline, "run a complete pass after the marker", func(f BackupFile) bool {
			return changed(f, v1) && f.PendingBytes == 0 && f.LagSeconds == 0
		})
		if err != nil {
			return Cut{}, err
		}
		cut.ShippedV0, cut.ShippedV1, cut.ShippedMs = v0.UnixMilli(), v1.UnixMilli(), f.LastShippedAt.UnixMilli()
	} else {
		cut.Note += "; unshipped: source verification only, NOT a PITR cut, never restorable"
	}
	cut.AtMs = (time.Now().UnixMilli()/1000 + 1) * 1000
	sleepCtx(ctx, time.Until(time.UnixMilli(cut.AtMs+1100)))
	if !unshipped {
		vw, err := lastShipped(ctx, c, r, deadline)
		if err != nil {
			return Cut{}, err
		}
		if err := nudge(ctx, c, r, name, "window"); err != nil {
			return Cut{}, err
		}
		f, err := waitShipped(ctx, c, r, deadline, "extend the restore window past the cut", func(f BackupFile) bool { return changed(f, vw) })
		if err != nil {
			return Cut{}, err
		}
		if cut.WindowMs = f.LastShippedAt.UnixMilli(); cut.WindowMs <= cut.AtMs {
			return Cut{}, fmt.Errorf("the restore window ends at %d, not after the cut at %d: the harness and node clocks disagree", cut.WindowMs, cut.AtMs)
		}
	}
	for p, n := range cut.LedgerLines {
		now, err := LedgerLines(r, p)
		if err != nil {
			return Cut{}, err
		}
		if now != n {
			return Cut{}, fmt.Errorf("the %s ledger went from %d to %d lines while the cut was taken: a writer ran, the cut is not quiesced", p, n, now)
		}
	}
	return cut, r.AddCut(cut)
}

// nudge writes a new document to the nudges collection: data for the shipper's next pass. Nudges are never verified.
func nudge(ctx context.Context, c *Client, r *Run, cut, why string) error {
	path := r.DatabasePath(r.Database) + "/collections/nudges/documents/by-key?id=" + ulid.Make().String()
	if _, err := c.JSON(ctx, http.MethodPut, path, map[string]any{"cut": cut, "why": why, "writtenMs": time.Now().UnixMilli()}, nil); err != nil {
		return fmt.Errorf("nudge: %w", err)
	}
	return nil
}

// changed reports a LastShippedAt present and different from before: a pass finished since before was read.
func changed(f BackupFile, before time.Time) bool {
	return f.LastShippedAt != nil && !f.LastShippedAt.Equal(before)
}

// lastShipped is the database file's LastShippedAt (zero before its first pass), once the node lists the file.
func lastShipped(ctx context.Context, c *Client, r *Run, deadline time.Time) (time.Time, error) {
	f, err := waitShipped(ctx, c, r, deadline, "list the database", func(BackupFile) bool { return true })
	if err != nil || f.LastShippedAt == nil {
		return time.Time{}, err
	}
	return *f.LastShippedAt, nil
}

func shippedMs(f BackupFile) int64 {
	if f.LastShippedAt == nil {
		return 0
	}
	return f.LastShippedAt.UnixMilli()
}

// waitShipped polls /v1/backup every 250 ms until ok holds for the run's database file, or fails at deadline. A file
// not listed yet is waited for too: the backup discovers new database files every 10 s (ContinuousBackup.cs:58).
func waitShipped(ctx context.Context, c *Client, r *Run, deadline time.Time, what string, ok func(BackupFile) bool) (BackupFile, error) {
	for {
		f, listed, err := c.BackupStatus(ctx, r.DatabaseID)
		if err != nil {
			return f, err
		}
		if listed && ok(f) {
			return f, nil
		}
		if time.Now().After(deadline) && !listed {
			return f, fmt.Errorf("the node does not list database %s in /v1/backup (waited to %s)", r.DatabaseID, what)
		}
		if time.Now().After(deadline) {
			return f, fmt.Errorf("backup did not %s in time (last shipped %d, lag %.1fs, pending %d, last error %q)", what, shippedMs(f), f.LagSeconds, f.PendingBytes, deref(f.LastError))
		}
		sleepCtx(ctx, 250*time.Millisecond)
		if ctx.Err() != nil {
			return f, ctx.Err()
		}
	}
}

// SingleNode fails unless the node reports no replication peers (GET /replication): the phase fence argument assumes
// one writer per database file, which peers would break.
func SingleNode(ctx context.Context, c *Client) error {
	var status struct {
		Peers *[]json.RawMessage `json:"peers"`
	}
	if _, err := c.JSON(ctx, http.MethodGet, "/replication", nil, &status); err != nil {
		return fmt.Errorf("single-node check: %w", err)
	}
	if status.Peers == nil || len(*status.Peers) > 0 {
		return fmt.Errorf("single-node check: the node reports replication peers (or none at all): the phase fence assumes one writer per database")
	}
	return nil
}

func expectedBatches(r *Run, p Phase) int64 {
	n := p.hi - p.lo
	if !p.Load() {
		n = int64(len(p.Members(r.Docs)))
	}
	return (n + BatchSize - 1) / BatchSize
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

// RefusalUnproven is the 409 code the A-7 fix is expected to answer for a time it cannot place in commit order (the
// restore owner's contract may still rename it: change it here only).
const RefusalUnproven = "restore_point_unproven"

// typedRefusals are the 409 error codes with which a restore refuses instead of restoring: history not provably
// contiguous up to T (D6), T not provably placed (A-7), or an object failing authentication or its integrity check.
var typedRefusals = []string{"backup_history_incomplete", RefusalUnproven, "backup_unreadable"}

// Restore outcomes: restored (201), refused:<code> (a typed refusal: neither a pass nor a loss), failed (anything else).
const (
	OutcomeRestored = "restored"
	OutcomeRefused  = "refused:"
	OutcomeFailed   = "failed"
)

// WindowLatest is the restore window's latest time on c's node, in ms. Read from the bucket it is the disaster-recovery
// latest point: the whole second at or before the start of the newest backup read marked in the newest segments.
func WindowLatest(ctx context.Context, r *Run, c *Client) (int64, error) {
	var window struct {
		Latest time.Time `json:"latest"`
	}
	if _, err := c.JSON(ctx, http.MethodGet, r.ManagementPath()+"/point-in-time", nil, &window); err != nil {
		return 0, err
	}
	return window.Latest.UnixMilli(), nil
}

// RestoreResult is the evidence of one restore request.
type RestoreResult struct {
	Outcome     string          `json:"outcome"`
	Cut         string          `json:"cut"`
	PointInTime string          `json:"point_in_time"`
	Name        string          `json:"name"`
	Window      json.RawMessage `json:"window"`
	Plan        json.RawMessage `json:"plan,omitempty"`
	PlanStatus  int             `json:"plan_status"`
	Status      int             `json:"status"`
	Answer      json.RawMessage `json:"answer"`
	Seconds     float64         `json:"seconds"`
	StartedMs   int64           `json:"started_ms"`
	Node        string          `json:"node"`
}

// Restore asks c's node to restore the run's database as of atMs into a new database called name, and records the
// window, the plan preview, the answer and its outcome under evidence/restore-<name>.json. A typed refusal is no error
// here (whether T had to restore is the caller's contract); any other answer than 201 is.
func Restore(ctx context.Context, r *Run, c *Client, cut string, atMs int64, name, node string) (RestoreResult, error) {
	if c, err := r.Cut(cut); err != nil {
		return RestoreResult{}, err
	} else if c.Kind == "unshipped" {
		return RestoreResult{}, fmt.Errorf("cut %s is unshipped (taken without backup): it is not a PITR cut and cannot be restored", cut)
	}
	db := r.ManagementPath()
	at := time.UnixMilli(atMs).UTC().Format("2006-01-02T15:04:05.000Z")
	res := RestoreResult{Cut: cut, PointInTime: at, Name: name, Node: node}
	if resp, err := c.Do(ctx, http.MethodGet, db+"/point-in-time", nil); err == nil {
		res.Window = rawOrString(resp.Body)
	}
	if resp, err := c.Do(ctx, http.MethodPost, db+"/point-in-time/plan", mustJSON(map[string]string{"pointInTime": at})); err == nil {
		res.PlanStatus, res.Plan = resp.Status, rawOrString(resp.Body)
	}
	start := time.Now()
	res.StartedMs = start.UnixMilli()
	log.Printf("restore %s as of %s into %s", r.Database, at, name)
	resp, err := c.Do(ctx, http.MethodPost, db+"/point-in-time/restore", mustJSON(map[string]string{"pointInTime": at, "name": name}))
	res.Seconds = time.Since(start).Seconds()
	res.Outcome = OutcomeFailed
	if err == nil {
		res.Status, res.Answer = resp.Status, rawOrString(resp.Body)
		var answer struct {
			Error struct {
				Code string `json:"code"`
			} `json:"error"`
		}
		_ = json.Unmarshal(resp.Body, &answer)
		switch {
		case resp.Status == http.StatusCreated:
			res.Outcome = OutcomeRestored
		case resp.Status == http.StatusConflict && slices.Contains(typedRefusals, answer.Error.Code):
			res.Outcome = OutcomeRefused + answer.Error.Code
		}
	}
	if werr := r.Evidence("restore-"+name+".json", res); werr != nil {
		return res, werr
	}
	if err != nil {
		return res, err
	}
	if res.Outcome == OutcomeFailed {
		return res, fmt.Errorf("restore answered HTTP %d %s", resp.Status, truncate(resp.Body, 400))
	}
	return res, nil
}

func mustJSON(v any) []byte {
	b, err := json.Marshal(v)
	if err != nil {
		panic(err)
	}
	return b
}

func rawOrString(b []byte) json.RawMessage {
	if json.Valid(b) {
		return b
	}
	q, _ := json.Marshal(string(b))
	return q
}

// Evidence writes evidence/<name> as indented JSON.
func (r *Run) Evidence(name string, v any) error {
	dir := filepath.Join(r.Dir, "evidence")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return writeJSON(filepath.Join(dir, name), v)
}

// FileSHA256 hashes a file.
func FileSHA256(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func jsonUnmarshal(b []byte, v any) error { return json.Unmarshal(b, v) }

func sortedKeys(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	slices.Sort(out)
	return out
}
