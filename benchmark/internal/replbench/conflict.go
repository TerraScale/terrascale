package replbench

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/oklog/ulid/v2"
)

// conflictBook records, per conflict document, the last round each region wrote successfully (-1: none). Each region
// writes its conflict documents from one goroutine, so its own writes to one document are ordered.
type conflictBook struct {
	mu   sync.Mutex
	ids  []string
	last map[string]map[string]int // id -> region -> last successful round
}

// seedConflicts creates the conflict documents in the first region and waits until every region serves them.
func (r *Runner) seedConflicts(ctx context.Context) error {
	r.conflicts.last = map[string]map[string]int{}
	coll := r.collection + "-conflict"
	for range r.cfg.ConflictDocs {
		id := ulid.Make().String()
		if resp, ok, _ := r.call(ctx, r.regions[0], http.MethodPut, DocPath(coll, id), []byte(`{"s":"seed"}`), "application/json"); !ok {
			return fmt.Errorf("seed conflict document: HTTP %d %s", resp.Status, resp.Body)
		}
		r.conflicts.ids = append(r.conflicts.ids, id)
		r.conflicts.last[id] = map[string]int{}
		for _, reg := range r.regions {
			r.conflicts.last[id][reg.Name] = -1
		}
	}
	for _, id := range r.conflicts.ids {
		for _, reg := range r.regions[1:] {
			if err := until(ctx, 2*time.Minute, func() bool {
				resp, err := reg.Do(ctx, http.MethodGet, DocPath(coll, id), nil, "")
				return err == nil && resp.Status == http.StatusOK
			}); err != nil {
				return fmt.Errorf("conflict seed on %s: %w", reg.Name, err)
			}
		}
	}
	return nil
}

// conflictWriter overwrites every conflict document ConflictRounds times, round by round, with a merge patch that sets
// the shared field s and this region's own field f_<region>.
func (r *Runner) conflictWriter(ctx context.Context, reg *Region) {
	coll := r.collection + "-conflict"
	for round := range r.cfg.ConflictRounds {
		for _, id := range r.conflicts.ids {
			body, _ := json.Marshal(map[string]any{"s": fmt.Sprintf("%s-%d", reg.Name, round), "f_" + reg.Name: round})
			began := time.Now()
			_, ok, _ := r.call(ctx, reg, http.MethodPatch, DocPath(coll, id), body, "application/merge-patch+json")
			r.patch[reg.Name].Record(time.Now().Sub(began))
			if ok {
				r.conflicts.mu.Lock()
				r.conflicts.last[id][reg.Name] = round
				r.conflicts.mu.Unlock()
			}
		}
	}
}

// ConflictOutcome tallies the conflict documents after convergence.
type ConflictOutcome struct {
	Docs      int            `json:"docs"`
	Identical int            `json:"identical"`  // every region holds the same bytes
	SameField map[string]int `json:"same_field"` // region whose last write of s survived; "invalid" when it is no region's last write
	OwnField  map[string]int `json:"own_field"`  // "kept" when f_<region> is that region's last round, else "lost"
	Diverged  []string       `json:"diverged,omitempty"`
}

// CheckConflicts reads every conflict document from every region and tallies the per-field outcomes.
func (r *Runner) CheckConflicts(ctx context.Context) (ConflictOutcome, error) {
	coll := r.collection + "-conflict"
	out := ConflictOutcome{Docs: len(r.conflicts.ids), SameField: map[string]int{}, OwnField: map[string]int{}}
	for _, id := range r.conflicts.ids {
		var copies [][]byte
		for _, reg := range r.regions {
			resp, err := reg.Do(ctx, http.MethodGet, DocPath(coll, id), nil, "")
			if err != nil || resp.Status != http.StatusOK {
				return out, fmt.Errorf("read conflict %s on %s: HTTP %d %v", id, reg.Name, resp.Status, err)
			}
			var env struct {
				Document json.RawMessage `json:"document"`
			}
			if err := json.Unmarshal(resp.Body, &env); err != nil {
				return out, err
			}
			c, err := Canonical(env.Document)
			if err != nil {
				return out, err
			}
			copies = append(copies, c)
		}
		if slices.ContainsFunc(copies, func(c []byte) bool { return string(c) != string(copies[0]) }) {
			if len(out.Diverged) < 10 {
				out.Diverged = append(out.Diverged, id)
			}
			continue
		}
		out.Identical++
		var doc map[string]any
		_ = json.Unmarshal(copies[0], &doc)
		last := r.conflicts.last[id]
		winner := "invalid"
		if s, _ := doc["s"].(string); s != "" {
			region, round, _ := strings.Cut(s, "-")
			if fmt.Sprint(last[region]) == round {
				winner = region
			}
		}
		out.SameField[winner]++
		for _, reg := range r.regions {
			if v, ok := doc["f_"+reg.Name].(float64); ok && int(v) == last[reg.Name] {
				out.OwnField[reg.Name+" kept"]++
			} else {
				out.OwnField[reg.Name+" lost"]++
			}
		}
	}
	return out, nil
}
