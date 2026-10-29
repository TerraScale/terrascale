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

func Setup(ctx context.Context, r *Run) error {
	if r.Organization == "" {
		r.Owner, r.Organization, r.Project, r.DatabaseID = ulid.Make().String(), ulid.Make().String(), ulid.Make().String(), ulid.Make().String()
		r.SetupCommit = ulid.Make().String()
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
	return createTenant(ctx, c, r)
}

func createTenant(ctx context.Context, c *Client, r *Run) error {

	commit := map[string]any{
		"id": r.SetupCommit,
		"ops": []any{
			map[string]any{"op": "create_organization", "id": r.Organization, "name": "pitr-" + r.ID},
			map[string]any{"op": "set_plan", "organization": r.Organization, "plan": "enterprise"},
			map[string]any{"op": "create_project", "organization": r.Organization, "id": r.Project, "name": "pitr"},
			map[string]any{"op": "create_database", "organization": r.Organization, "project": r.Project, "name": r.Database, "id": r.DatabaseID},
		},
	}
	if _, err := c.JSON(ctx, http.MethodPost, "/v1/management/commits", commit, nil); err != nil {
		return fmt.Errorf("tenant: %w", err)
	}
	db := r.DatabasePath(r.Database)

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

func created(resp Response, err error) error {
	if err != nil && resp.Status == http.StatusConflict && (bytes.Contains(resp.Body, []byte(`"name_taken"`)) || bytes.Contains(resp.Body, []byte(`"index_exists"`))) {
		return nil
	}
	return err
}

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

func nudge(ctx context.Context, c *Client, r *Run, cut, why string) error {
	path := r.DatabasePath(r.Database) + "/collections/nudges/documents/by-key?id=" + ulid.Make().String()
	if _, err := c.JSON(ctx, http.MethodPut, path, map[string]any{"cut": cut, "why": why, "writtenMs": time.Now().UnixMilli()}, nil); err != nil {
		return fmt.Errorf("nudge: %w", err)
	}
	return nil
}

func changed(f BackupFile, before time.Time) bool {
	return f.LastShippedAt != nil && !f.LastShippedAt.Equal(before)
}

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

const RefusalUnproven = "restore_point_unproven"

var typedRefusals = []string{"backup_history_incomplete", RefusalUnproven, "backup_unreadable"}

const (
	OutcomeRestored = "restored"
	OutcomeRefused  = "refused:"
	OutcomeFailed   = "failed"
)

func WindowLatest(ctx context.Context, r *Run, c *Client) (int64, error) {
	var window struct {
		Latest time.Time `json:"latest"`
	}
	if _, err := c.JSON(ctx, http.MethodGet, r.ManagementPath()+"/point-in-time", nil, &window); err != nil {
		return 0, err
	}
	return window.Latest.UnixMilli(), nil
}

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

func (r *Run) Evidence(name string, v any) error {
	dir := filepath.Join(r.Dir, "evidence")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return writeJSON(filepath.Join(dir, name), v)
}

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
