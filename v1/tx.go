package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
)

// Tx builds an atomic transaction of up to 1024 operations across the
// collections of one database. Ops never target the same document twice.
// A Tx is not safe for concurrent use.
type Tx struct {
	db  *Database
	ops []txOp
	err error
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
	Absent      bool            `json:"absent,omitempty"`
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
	ID       string  `json:"id,omitempty"`
	Version  string  `json:"version,omitempty"`
	Asserted bool    `json:"asserted,omitempty"`
	Count    *Uint64 `json:"count,omitempty"`
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
		if err := checkID(id); err != nil {
			return fail(err)
		}
	}
	for _, p := range pre {
		if p.ifMatch != "" {
			if err := checkVersion(p.ifMatch); err != nil {
				return fail(err)
			}
			op.IfMatch = p.ifMatch
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
// the server generate a UUIDv7, returned in the op's result.
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
// to satisfy bounds. The filter is required (the contract defines no
// filter-less count). It is evaluated inside the commit before this
// transaction's writes, so it is serializable with every other write to the
// database (for example "at most 24 tokens per user"). A filter that would
// examine more than 10000 entries fails with ErrAssertRequiresIndex; a
// transaction holds at most 16 AssertCount ops.
func (t *Tx) AssertCount(collection string, filter Filter, bounds CountBounds) *Tx {
	idx := len(t.ops)
	if bounds.Gte == nil && bounds.Lte == nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: AssertCount needs Gte or Lte", idx))
	}
	if filter.IsZero() {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: AssertCount needs a filter", idx))
	}
	op := txOp{Op: "assert_count", Filter: &filter, Gte: bounds.Gte, Lte: bounds.Lte}
	if err := checkName("collection", collection); err != nil {
		t.err = errors.Join(t.err, fmt.Errorf("op %d: %w", idx, err))
	}
	op.Collection = collection
	t.ops = append(t.ops, op)
	return t
}

// Commit applies every op atomically. Failures are *APIError values whose Op
// names the failing op index. Only IdempotencyKey is accepted as an option;
// preconditions belong to individual ops.
func (t *Tx) Commit(ctx context.Context, opts ...WriteOption) (*TxResult, error) {
	if t.err != nil {
		return nil, fmt.Errorf("terrabase: transaction: %w", t.err)
	}
	if len(t.ops) == 0 {
		return nil, errors.New("terrabase: transaction has no ops")
	}
	if err := checkName("database", t.db.name); err != nil {
		return nil, err
	}
	w, err := resolveWrite(opts)
	if err != nil {
		return nil, err
	}
	if w.ifMatch != "" || w.ifNoneMatchAny {
		return nil, errors.New("terrabase: Commit takes no preconditions; pass them to the ops")
	}
	req := request{write: true, method: http.MethodPost, path: []string{"v1", "databases", t.db.name, "transactions"}, idemKey: w.idemKey}
	var res TxResult
	if _, err := t.db.c.call(ctx, req, struct {
		Ops []txOp `json:"ops"`
	}{t.ops}, &res); err != nil {
		return nil, err
	}
	return &res, nil
}
