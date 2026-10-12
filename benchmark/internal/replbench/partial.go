package replbench

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"
	"time"
)

// A multi-machine run (one load machine per region on Fly) splits the runner in three steps: setup writes a State, each
// region's machine loads with Config.Only and writes a Partial, and verify merges the partials and checks every region.

// State is what setup created: the collections and the conflict documents.
type State struct {
	Collection  string   `json:"collection"`
	ConflictIDs []string `json:"conflict_ids"`
}

// State answers what Setup created.
func (r *Runner) State() State { return State{Collection: r.collection, ConflictIDs: r.conflicts.ids} }

// Attach continues from another process's Setup.
func (r *Runner) Attach(s State) {
	r.collection = s.Collection
	r.conflicts.ids = s.ConflictIDs
	r.conflicts.last = map[string]map[string]int{}
	for _, id := range s.ConflictIDs {
		r.conflicts.last[id] = map[string]int{}
		for _, reg := range r.regions {
			r.conflicts.last[id][reg.Name] = -1
		}
	}
}

// Partial is one region's load: what it wrote and measured.
type Partial struct {
	Region       string            `json:"region"`
	Start        time.Time         `json:"start"`
	End          time.Time         `json:"end"`
	Writes       RegionWrites      `json:"writes"`
	Lag          map[string]Lag    `json:"lag"`
	ProbeRead    Summary           `json:"probe_read_latency"`
	Expected     map[string]string `json:"expected"` // id -> base64 hash
	Deleted      int               `json:"deleted"`
	Unknown      []string          `json:"unknown"`
	ConflictLast map[string]int    `json:"conflict_last"` // conflict id -> this region's last successful round
}

// Partial answers the load of Config.Only after Load.
func (r *Runner) Partial(start time.Time, loadSeconds float64) Partial {
	var res Result
	r.Collect(&res, loadSeconds)
	p := Partial{Region: r.cfg.Only, Start: start, End: time.Now(), Writes: res.Writes[r.cfg.Only], Lag: map[string]Lag{}, ProbeRead: res.ProbeRead,
		Expected: make(map[string]string, len(r.expected)), Deleted: r.deleted, ConflictLast: map[string]int{}}
	for key, l := range res.Lag {
		if l.Count > 0 || l.Missing > 0 {
			p.Lag[key] = l
		}
	}
	for id, h := range r.expected {
		p.Expected[id] = base64.StdEncoding.EncodeToString(h[:])
	}
	for id := range r.unknown {
		p.Unknown = append(p.Unknown, id)
	}
	for id, last := range r.conflicts.last {
		p.ConflictLast[id] = last[r.cfg.Only]
	}
	return p
}

// Merge loads every region's partial as if this runner had written them all.
func (r *Runner) Merge(parts []Partial) error {
	for _, p := range parts {
		for id, b := range p.Expected {
			raw, err := base64.StdEncoding.DecodeString(b)
			if err != nil || len(raw) != len(Hash{}) {
				return fmt.Errorf("partial %s: hash of %s is not 16 bytes of base64", p.Region, id)
			}
			r.expected[id] = Hash(raw)
		}
		for _, id := range p.Unknown {
			r.unknown[id] = true
		}
		r.deleted += p.Deleted
		for id, round := range p.ConflictLast {
			if last, ok := r.conflicts.last[id]; ok {
				last[p.Region] = round
			}
		}
	}
	return nil
}

// FromPartials fills a result's per-region writes, lag and load window from the partials.
func FromPartials(res *Result, parts []Partial) {
	res.Writes, res.Lag = map[string]RegionWrites{}, map[string]Lag{}
	var start, end time.Time
	for _, p := range parts {
		res.Writes[p.Region] = p.Writes
		for key, l := range p.Lag {
			res.Lag[key] = l
		}
		if p.ProbeRead.P99 > res.ProbeRead.P99 {
			res.ProbeRead = p.ProbeRead // the worst region's reads: the coarsest lag resolution
		}
		if start.IsZero() || p.Start.Before(start) {
			start = p.Start
		}
		if p.End.After(end) {
			end = p.End
		}
	}
	res.LoadSeconds = end.Sub(start).Seconds()
}

// WriteJSON and ReadJSON save and load a State or a Partial.
func WriteJSON(path string, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return os.WriteFile(path, b, 0o600)
}

func ReadJSON(path string, v any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}
