package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"

	"terrascale.tech/sdk/internal/entityid"
)

// Tx builds an atomic transaction of up to 1024 operations across the
// collections of one database. Ops never target the same document twice.
// A Tx is not safe for concurrent use.
type Tx struct {
	db         *Database
	ops        []txOp
	err        error
	durability Durability
}

type txOp struct {
	Op          string          `json:"op"`
	Collection  string          `json:"collection"`
	ID          string          `json:"id,omitempty"`
	Document    json.RawMessage `json:"document,omitempty"`
	IfMatch     string          `json:"if_match,omitempty"`
	IfNoneMatch string          `json:"if_none_match,omitempty"`
	Merge       json.RawMessage `json:"merge,omitempty"`
	Ops         []PatchOp       `json:"ops,omitempty"`
	Filter      *Filter         `json:"filter,omitempty"`
	Absent      bool            `json:"absent,omitzero"`
	Gte         *uint64         `json:"gte,omitempty"`
	Lte         *uint64         `json:"lte,omitempty"`
}

// TxResult is a committed transaction.
type TxResult struct {
	Offset     Uint64     `json:"offset"`
	Durability Durability `json:"durability"`
	// Results has one entry per op, in order.
	Results []OpResult `json:"results"`
}

// OpResult is the outcome of one transaction op: ID and Version for writes,
// Asserted for asserts, and Count for AssertCount.
type OpResult struct {
	// Document is the stored object returned by a patch op.
	Document json.RawMessage `json:"document,omitempty"`
	ID       string          `json:"id,omitempty" documentkey:"optional"`
	Version  string          `json:"version,omitempty"`
	Asserted bool            `json:"asserted,omitzero"`
	Count    *Uint64         `json:"count,omitempty"`
}

// CountBounds bounds an AssertCount: the count must be at least Gte and at
// most Lte. At least one bound is required.
type CountBounds struct {
	Gte, Lte *uint64
}

// Tx starts a transaction builder.
func (d *Database) Tx() *Tx { return &Tx{db: d} }

// Len returns the number of ops added so far.
func (t *Tx) Len() int { return len(t.ops) }

func (t *Tx) add(op txOp, pre []Precondition, collection, id string, idRequired bool) *Tx {
	return t.addOp(op, pre, collection, id, idRequired, true)
}

func (t *Tx) addOp(op txOp, pre []Precondition, collection, id string, idRequired, allowNoneMatch bool) *Tx {
	idx := len(t.ops)
	fail := func(err error) *Tx {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", idx, err))
		t.ops = append(t.ops, op)
		return t
	}
	if err := checkName("collection", collection); err != nil {
		return fail(err)
	}
	if id != "" || idRequired {
		if err := entityid.DocumentKey(id); err != nil {
			return fail(fmt.Errorf("document ID: %w", err))
		}
	}
	for _, p := range pre {
		if p.ifMatch != "" {
			if err := checkVersion(p.ifMatch); err != nil {
				return fail(err)
			}
			op.IfMatch = `"` + p.ifMatch + `"`
		}
		if p.ifNoneMatchAny {
			if !allowNoneMatch {
				return fail(fmt.Errorf("%s does not take IfNoneMatchAny", op.Op))
			}
			op.IfNoneMatch = "*"
		}
	}
	if op.IfMatch != "" && op.IfNoneMatch != "" {
		return fail(errors.New("IfMatch and IfNoneMatchAny are mutually exclusive"))
	}
	op.Collection, op.ID = collection, id
	t.ops = append(t.ops, op)
	return t
}

// Create adds a create op (a put that requires absence). An empty id lets
// the server generate a ULID, returned in the op's result.
func (t *Tx) Create(collection, id string, doc any) *Tx {
	body, err := encodeObject(doc)
	if err != nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", len(t.ops), err))
	}
	return t.add(txOp{Op: "create", Document: body}, nil, collection, id, false)
}

// Put adds a full-document write with optional preconditions.
func (t *Tx) Put(collection, id string, doc any, pre ...Precondition) *Tx {
	body, err := encodeObject(doc)
	if err != nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", len(t.ops), err))
	}
	return t.add(txOp{Op: "put", Document: body}, pre, collection, id, true)
}

// Patch adds a MergePatch or Ops patch with an optional IfMatch;
// IfNoneMatchAny is rejected.
func (t *Tx) Patch(collection, id string, patch Patch, pre ...Precondition) *Tx {
	if patch.err != nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", len(t.ops), patch.err))
	}
	if patch.merge == nil && patch.ops == nil && patch.err == nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: empty Patch", len(t.ops)))
	}
	return t.addOp(txOp{Op: "patch", Merge: patch.merge, Ops: patch.ops}, pre, collection, id, true, false)
}

// Delete adds a delete with an optional IfMatch; IfNoneMatchAny is rejected.
func (t *Tx) Delete(collection, id string, pre ...Precondition) *Tx {
	return t.addOp(txOp{Op: "delete"}, pre, collection, id, true, false)
}

// Assert requires the document to exist, be unconflicted and match filter,
// without writing it.
func (t *Tx) Assert(collection, id string, filter Filter) *Tx {
	if filter.IsZero() {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: Assert needs a filter", len(t.ops)))
	}
	return t.add(txOp{Op: "assert", Filter: &filter}, nil, collection, id, true)
}

// AssertAbsent requires the document to be absent, deleted or expired.
func (t *Tx) AssertAbsent(collection, id string) *Tx {
	return t.add(txOp{Op: "assert", Absent: true}, nil, collection, id, true)
}

// AssertCount requires the number of documents in collection matching filter
// to satisfy bounds. A zero filter counts every document. It is evaluated
// inside the commit before this transaction's writes, so it is serializable with every other write to the
// database (for example "at most 24 tokens per user"). A filter that would
// examine more than 10000 entries fails with ErrAssertRequiresIndex; a
// transaction holds at most 16 AssertCount ops.
func (t *Tx) AssertCount(collection string, filter Filter, bounds CountBounds) *Tx {
	idx := len(t.ops)
	if bounds.Gte == nil && bounds.Lte == nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: AssertCount needs Gte or Lte", idx))
	}
	op := txOp{Op: "assert_count", Gte: bounds.Gte, Lte: bounds.Lte}
	if !filter.IsZero() {
		op.Filter = &filter
	}
	if err := checkName("collection", collection); err != nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", idx, err))
	}
	op.Collection = collection
	t.ops = append(t.ops, op)
	return t
}

// Commit applies every op atomically. Failures are *APIError values whose Op
// names the failing op index. Preconditions belong to individual ops. The
// client retries a commit only when every write op has an IfMatch version.
// Transport failures and ErrCommitUncertain are returned without a retry.
func (t *Tx) Commit(ctx context.Context) (*TxResult, error) {
	if t.err != nil {
		return nil, fmt.Errorf("terrabase: transaction: %w", t.err)
	}
	if len(t.ops) == 0 {
		return nil, errors.New("terrabase: transaction has no ops")
	}
	p, err := t.db.path("transactions")
	if err != nil {
		return nil, err
	}
	req := request{method: http.MethodPost, path: p, replaySafe: t.conditional(), header: writeConfig{durability: t.durability}.header()}
	var res TxResult
	if _, err := t.db.c.call(ctx, req, struct {
		Ops []txOp `json:"ops"`
	}{t.ops}, &res); err != nil {
		return nil, err
	}
	if len(res.Results) != len(t.ops) {
		return nil, errors.New("terrabase: transaction response must have one result per op")
	}
	for i, op := range t.ops {
		if op.Op == "assert" || op.Op == "assert_count" {
			if !res.Results[i].Asserted || (op.Op == "assert_count" && res.Results[i].Count == nil) {
				return nil, fmt.Errorf("terrabase: transaction result %d is missing its assertion outcome", i)
			}
			continue
		}
		if err := checkVersion(res.Results[i].Version); err != nil {
			return nil, fmt.Errorf("terrabase: invalid transaction result %d: %w", i, err)
		}
		if op.Op == "patch" {
			if err := checkResponseObject(res.Results[i].Document); err != nil {
				return nil, fmt.Errorf("terrabase: transaction result %d document: %w", i, err)
			}
		}
		check := entityid.DocumentKey
		if op.Op == "create" && op.ID == "" {
			check = ValidateID
		}
		if err := check(res.Results[i].ID); err != nil {
			return nil, fmt.Errorf("terrabase: invalid transaction result %d: %w", i, err)
		}
	}
	return &res, nil
}

// conditional reports whether a replay of the transaction cannot apply twice:
// every write op requires a version. Absence is not enough, since a tombstone
// left by another writer satisfies it again.
func (t *Tx) conditional() bool {
	for _, op := range t.ops {
		if op.Op != "assert" && op.Op != "assert_count" && op.IfMatch == "" {
			return false
		}
	}
	return true
}

// UnmarshalJSON validates the required transaction acknowledgement.
func (r *TxResult) UnmarshalJSON(data []byte) error {
	if err := requiredResponseMembers(data, "offset", "durability", "results"); err != nil {
		return err
	}
	type wire TxResult
	var decoded wire
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	if decoded.Durability == "" {
		return errors.New("terrabase: response durability must not be empty")
	}
	*r = TxResult(decoded)
	return nil
}
