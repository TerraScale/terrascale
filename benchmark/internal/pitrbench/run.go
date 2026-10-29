package pitrbench

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"time"
)

// BatchSize is the documents per transaction: one atomic commit, under the 1024-op and 7 MiB transaction limits.
const BatchSize = 1000

// Run is run.json in the run directory: everything an experiment is pinned to. It holds no secrets; credentials stay
// in the 0600 env file the node reads, which this program never prints.
type Run struct {
	Dir          string    `json:"-"`
	NodeDir      string    `json:"-"` // another node's directory for this invocation (see nodeDir)
	ID           string    `json:"run_id"`
	Seed         uint64    `json:"seed"`
	Docs         int64     `json:"docs"`
	Partitions   int       `json:"partitions"`
	LoadSplit    int64     `json:"load_split"` // load-a is [0, LoadSplit), load-b is [LoadSplit, Docs)
	Organization string    `json:"organization"`
	Project      string    `json:"project"`
	Database     string    `json:"database"`
	DatabaseID   string    `json:"database_id"`
	Owner        string    `json:"owner"`
	Collection   string    `json:"collection"`
	Commit       string    `json:"commit"`
	Binary       string    `json:"binary"`
	BinarySHA256 string    `json:"binary_sha256"`
	EnvFile      string    `json:"env_file"`
	Environment  string    `json:"environment"`
	Target       string    `json:"target"` // "tigris" or "local-s3-emulator": never conflate the two in a report
	Created      time.Time `json:"created"`
}

// LoadRun reads run.json from dir.
func LoadRun(dir string) (*Run, error) {
	b, err := os.ReadFile(filepath.Join(dir, "run.json"))
	if err != nil {
		return nil, err
	}
	var r Run
	if err := json.Unmarshal(b, &r); err != nil {
		return nil, fmt.Errorf("run.json: %w", err)
	}
	r.Dir = dir
	return &r, nil
}

// Save writes run.json atomically.
func (r *Run) Save() error { return writeJSON(filepath.Join(r.Dir, "run.json"), r) }

func writeJSON(path string, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	tmp := path + ".partial"
	if err := os.WriteFile(tmp, append(b, '\n'), 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// Phase is one write phase. Load phases insert a contiguous index range; the others touch a fixed stride of indexes.
type Phase struct {
	Name string
	Op   string // create, put or delete
	Rev  int    // body revision written; 0 for delete
	lo   int64  // load range
	hi   int64
	has  func(i int64) bool // mutation membership
}

// Phases are the run's write phases in order. u1 updates 1 in 1000 documents, d1 deletes another 1 in 1000 plus 1 in
// 10 of the updated ones, r1 re-creates 1 in 10 of the deleted ones: every cut around them has updates, tombstones
// (including over updates) and deliberate re-creations whose absence or presence it must reproduce exactly.
func (r *Run) Phases() []Phase {
	return []Phase{
		{Name: "load-a", Op: "create", Rev: 1, lo: 0, hi: r.LoadSplit},
		{Name: "load-b", Op: "create", Rev: 1, lo: r.LoadSplit, hi: r.Docs},
		{Name: "u1", Op: "put", Rev: 2, has: func(i int64) bool { return i%1000 == 7 }},
		{Name: "d1", Op: "delete", has: func(i int64) bool { return i%1000 == 13 || i%10000 == 7 }},
		{Name: "r1", Op: "create", Rev: 3, has: func(i int64) bool { return i%10000 == 13 }},
	}
}

// Phase looks a phase up by name.
func (r *Run) Phase(name string) (Phase, error) {
	for _, p := range r.Phases() {
		if p.Name == name {
			return p, nil
		}
	}
	return Phase{}, fmt.Errorf("unknown phase %q", name)
}

// Members are the document indexes of a phase in ascending order; slot k of the phase is Members()[k].
func (p Phase) Members(docs int64) []int64 {
	if p.has == nil {
		out := make([]int64, 0, p.hi-p.lo)
		for i := p.lo; i < p.hi; i++ {
			out = append(out, i)
		}
		return out
	}
	var out []int64
	for i := range docs {
		if p.has(i) {
			out = append(out, i)
		}
	}
	return out
}

// Contains reports whether the phase touches document i.
func (p Phase) Contains(i int64) bool {
	if p.has == nil {
		return i >= p.lo && i < p.hi
	}
	return p.has(i)
}

// Load reports whether this is a contiguous insert phase (slot = i - lo).
func (p Phase) Load() bool { return p.has == nil }

// Cut is one recorded restore point. Exact cuts are taken quiesced: every phase in Applied is fully acknowledged and
// shipped before At, no other phase was written, and nothing was written between their last commit and At+1.1 s
// except the cut's marker and nudges. A bounded cut is taken while Partial is being written: the restore must hold,
// per writer stream, a prefix of Partial's batches.
type Cut struct {
	Name    string   `json:"name"`
	AtMs    int64    `json:"at_ms"`
	Kind    string   `json:"kind"` // exact, bounded, or unshipped (source verification only, never restorable)
	Applied []string `json:"applied"`
	Partial string   `json:"partial,omitempty"`
	// ShippedV0, V1 and ShippedMs (V2) are the LastShippedAt values of the quiesce (see TakeCut); WindowMs the one the
	// post-cut nudge produced, past AtMs.
	ShippedV0 int64 `json:"shipped_v0_ms,omitempty"`
	ShippedV1 int64 `json:"shipped_v1_ms,omitempty"`
	ShippedMs int64 `json:"shipped_ms,omitempty"`
	WindowMs  int64 `json:"window_ms,omitempty"`
	LastAckMs int64 `json:"last_ack_ms,omitempty"`
	// MarkerMs is when the marker's create was sent: a restore before it cannot hold the marker.
	MarkerMs int64 `json:"marker_ms,omitempty"`
	// LedgerLines is each phase ledger's line count when the cut was recorded: the cut is defined against exactly that
	// ledger prefix, so later attempts can never change what it expects.
	LedgerLines map[string]int `json:"ledger_lines,omitempty"`
	Note        string         `json:"note,omitempty"`
}

// Cuts reads cuts.jsonl.
func (r *Run) Cuts() ([]Cut, error) {
	f, err := os.Open(filepath.Join(r.Dir, "cuts.jsonl"))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var out []Cut
	s := bufio.NewScanner(f)
	for s.Scan() {
		var c Cut
		if err := json.Unmarshal(s.Bytes(), &c); err != nil {
			return nil, fmt.Errorf("cuts.jsonl: %w", err)
		}
		out = append(out, c)
	}
	return out, s.Err()
}

// Cut looks a cut up by name.
func (r *Run) Cut(name string) (Cut, error) {
	cuts, err := r.Cuts()
	if err != nil {
		return Cut{}, err
	}
	for _, c := range cuts {
		if c.Name == name {
			return c, nil
		}
	}
	return Cut{}, fmt.Errorf("unknown cut %q", name)
}

// AddCut appends a cut durably; names are unique.
func (r *Run) AddCut(c Cut) error {
	if _, err := r.Cut(c.Name); err == nil {
		return fmt.Errorf("cut %q exists", c.Name)
	}
	return appendLine(filepath.Join(r.Dir, "cuts.jsonl"), c)
}

func appendLine(path string, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return err
	}
	if _, err := f.Write(append(b, '\n')); err != nil {
		f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		f.Close()
		return err
	}
	return f.Close()
}

// Expected is what document i must be at a cut: absent, or alive at Rev. Phase and Slot name the last phase that
// touched it (a delete included), whose version file holds the version the database returned for it. Deleted marks a
// tombstone the restore must not resurrect.
type Expected struct {
	Alive   bool
	Deleted bool
	Rev     int
	Phase   string
	Slot    int64
}

// Oracle answers Expected for every index at one cut. Mutation slots are found by binary search in their member list.
type Oracle struct {
	run     *Run
	applied map[string]bool
	members map[string][]int64
	phases  []Phase
}

// NewOracle builds the oracle of the given applied phases (in run order).
func NewOracle(r *Run, applied []string) (*Oracle, error) {
	o := &Oracle{run: r, applied: map[string]bool{}, members: map[string][]int64{}}
	for _, name := range applied {
		if _, err := r.Phase(name); err != nil {
			return nil, err
		}
		o.applied[name] = true
	}
	for _, p := range r.Phases() {
		if !o.applied[p.Name] {
			continue
		}
		o.phases = append(o.phases, p)
		if !p.Load() {
			o.members[p.Name] = p.Members(r.Docs)
		}
	}
	return o, nil
}

// At is document i's expected state after the applied phases.
func (o *Oracle) At(i int64) Expected {
	var e Expected
	for _, p := range o.phases {
		if !p.Contains(i) {
			continue
		}
		slot := i - p.lo
		if !p.Load() {
			k, _ := slices.BinarySearch(o.members[p.Name], i)
			slot = int64(k)
		}
		if p.Op == "delete" {
			e = Expected{Deleted: true, Phase: p.Name, Slot: slot}
			continue
		}
		e = Expected{Alive: true, Rev: p.Rev, Phase: p.Name, Slot: slot}
	}
	return e
}
