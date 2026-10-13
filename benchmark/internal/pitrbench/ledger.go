package pitrbench

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
)

// Ledger events, keyed by operation identity (phase, batch) in append order. A batch is attempted (start, the
// intent, fsynced before the request) any number of times and ends in at most one terminal event: ack (a 200 answer
// with its versions) or present (a 412 after which reads prove every write of the batch in place). rejected is a 4xx
// that applied nothing. retry marks an ambiguous attempt; unresolved marks a batch this invocation could not prove
// either way. A batch without a terminal event is unresolved: never counted as written, failed or absent.
//
// Attempts count client-issued requests. Go's HTTP/2 transport may itself resend a POST only when the server refused
// the stream before processing it (REFUSED_STREAM, or a GOAWAY naming it unprocessed); such a resend is the identical
// conditional request, which the preconditions and the phase fence cover like any other attempt.
const (
	EvStart      = "start"
	EvAck        = "ack"
	EvPresent    = "present"
	EvRejected   = "rejected"
	EvRetry      = "retry"
	EvUnresolved = "unresolved"
)

// Event is one ledger line: the independent record of what the database acknowledged, written and fsynced before the
// writer moves on, so a crash of the harness never loses an acknowledgement.
type Event struct {
	Phase   string `json:"ph"`
	Batch   int64  `json:"b"`
	Inv     string `json:"inv,omitempty"` // the invocation of a start: streams are numbered per invocation
	Stream  int    `json:"s"`
	Attempt int    `json:"a"`
	Ev      string `json:"ev"`
	// Ms is the harness host's clock: a start's is taken before its fsync and send (the first start is the batch's send
	// time), an ack's when its 2xx arrived, a present's when its proof by read completed; any other when it is written.
	Ms     int64  `json:"t"`
	Status int    `json:"st,omitempty"`
	Offset string `json:"off,omitempty"`
	Note   string `json:"n,omitempty"`
}

// Ledger appends events to ledger/<phase>.jsonl, each fsynced.
type Ledger struct {
	mu sync.Mutex
	f  *os.File
}

// OpenLedger opens a phase's ledger for appending.
func OpenLedger(r *Run, phase string) (*Ledger, error) {
	if err := os.MkdirAll(filepath.Join(r.Dir, "ledger"), 0o700); err != nil {
		return nil, err
	}
	f, err := os.OpenFile(filepath.Join(r.Dir, "ledger", phase+".jsonl"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return nil, err
	}
	return &Ledger{f: f}, nil
}

// Append writes and fsyncs one event.
func (l *Ledger) Append(e Event) error {
	b, err := json.Marshal(e)
	if err != nil {
		return err
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if _, err := l.f.Write(append(b, '\n')); err != nil {
		return err
	}
	return l.f.Sync()
}

// Close closes the ledger.
func (l *Ledger) Close() error { return l.f.Close() }

// BatchState summarizes one batch from its ledger lines.
type BatchState struct {
	Inv       string // invocation and stream of the last start: per (Inv, Stream) the batches ran one at a time
	Stream    int
	Attempts  int
	FirstMs   int64 // first start
	LastStart int64 // start of the attempt that ended it
	EndMs     int64 // terminal event time, 0 while unknown
	Terminal  string
	Retries   int  // ambiguous attempts
	Flagged   bool // an unresolved event was recorded
	// AmbiguousMs is the latest time an attempt that did not end the batch was sent or found ambiguous (its retry or
	// unresolved event, or the start of an attempt a crash cut off): such an attempt may still apply later.
	AmbiguousMs int64
	open        int64 // start of an attempt with no outcome yet
}

// Ambiguous reports whether any attempt of the batch other than the one that ended it may have applied.
func (b *BatchState) Ambiguous() bool { return b.AmbiguousMs > 0 }

// ReadLedger folds a phase's ledger into per-batch states.
func ReadLedger(r *Run, phase string) (map[int64]*BatchState, error) {
	f, err := os.Open(filepath.Join(r.Dir, "ledger", phase+".jsonl"))
	if errors.Is(err, os.ErrNotExist) {
		return map[int64]*BatchState{}, nil
	}
	if err != nil {
		return nil, err
	}
	defer f.Close()
	out := map[int64]*BatchState{}
	s := bufio.NewScanner(f)
	s.Buffer(make([]byte, 64<<10), 1<<20)
	for s.Scan() {
		var e Event
		if err := json.Unmarshal(s.Bytes(), &e); err != nil {
			// Fails closed on any unparsable line, a torn last line from a crash included: the ledger is the oracle, so it
			// is repaired by hand, never truncated silently.
			return out, fmt.Errorf("ledger %s: %w", phase, err)
		}
		b := out[e.Batch]
		if b == nil {
			b = &BatchState{Stream: e.Stream, FirstMs: e.Ms}
			out[e.Batch] = b
		}
		switch e.Ev {
		case EvStart:
			b.AmbiguousMs = max(b.AmbiguousMs, b.open) // the previous attempt never got an outcome: a crash cut it off
			b.Attempts++
			b.LastStart, b.open, b.Inv, b.Stream = e.Ms, e.Ms, e.Inv, e.Stream
		case EvRetry:
			b.Retries++
			b.AmbiguousMs, b.open = max(b.AmbiguousMs, e.Ms), 0
		case EvUnresolved:
			b.Flagged = true
			b.AmbiguousMs, b.open = max(b.AmbiguousMs, e.Ms), 0
		case EvAck, EvPresent, EvRejected:
			if b.Terminal != "" && b.Terminal != e.Ev {
				return out, fmt.Errorf("ledger %s batch %d: second terminal event %s after %s", phase, e.Batch, e.Ev, b.Terminal)
			}
			b.Terminal, b.EndMs, b.open = e.Ev, e.Ms, 0
		}
	}
	for _, b := range out {
		b.AmbiguousMs = max(b.AmbiguousMs, b.open) // the last attempt never got an outcome
	}
	return out, s.Err()
}

// LedgerLines counts a phase ledger's lines: a cut pins the ledger prefix it was taken against.
func LedgerLines(r *Run, phase string) (int, error) {
	b, err := os.ReadFile(filepath.Join(r.Dir, "ledger", phase+".jsonl"))
	if errors.Is(err, os.ErrNotExist) {
		return 0, nil
	}
	if err != nil {
		return 0, err
	}
	n := 0
	for _, c := range b {
		if c == '\n' {
			n++
		}
	}
	return n, nil
}

// versionSlot is the fixed width of one recorded version: a length byte and up to 63 bytes.
const versionSlot = 64

// Versions is versions/<phase>.bin: the version the database returned for each slot of a phase, at offset
// slot*64, written before the batch's terminal ledger event. Zero length means not recorded.
type Versions struct{ f *os.File }

// OpenVersions opens a phase's version file for reading and writing.
func OpenVersions(r *Run, phase string) (*Versions, error) {
	if err := os.MkdirAll(filepath.Join(r.Dir, "versions"), 0o700); err != nil {
		return nil, err
	}
	f, err := os.OpenFile(filepath.Join(r.Dir, "versions", phase+".bin"), os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, err
	}
	return &Versions{f: f}, nil
}

// Put records the versions of consecutive slots starting at first.
func (v *Versions) Put(first int64, versions []string) error {
	buf := make([]byte, versionSlot*len(versions))
	for k, s := range versions {
		if len(s) == 0 || len(s) >= versionSlot {
			return fmt.Errorf("version %q does not fit a %d-byte slot", s, versionSlot)
		}
		buf[k*versionSlot] = byte(len(s))
		copy(buf[k*versionSlot+1:], s)
	}
	_, err := v.f.WriteAt(buf, first*versionSlot)
	return err
}

// PutAt records one slot's version (mutation phases, whose slots are not always consecutive on resolution).
func (v *Versions) PutAt(slot int64, version string) error { return v.Put(slot, []string{version}) }

// Sync flushes recorded versions to disk.
func (v *Versions) Sync() error { return v.f.Sync() }

// Get reads one slot's version, "" when none was recorded.
func (v *Versions) Get(slot int64) (string, error) {
	var b [versionSlot]byte
	n, err := v.f.ReadAt(b[:], slot*versionSlot)
	if n < versionSlot {
		return "", nil // beyond the end: never written
	}
	if err != nil {
		return "", err
	}
	return string(b[1 : 1+int(b[0])]), nil
}

// Close closes the file.
func (v *Versions) Close() error { return v.f.Close() }
