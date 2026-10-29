package v1

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"iter"
	"net/http"
)

// IDPath is the pseudo-path for the document ID.
const IDPath = "$id"

// Path is a filter or sort path: an RFC 6901 JSON Pointer string or IDPath.
// TerraBase does not serve {"lower":…} paths yet.
type Path interface{ ~string }

func pathValue[P Path](p P) any { return any(p) }

// Filter is a query-v1 filter expression, built with And, Or, Not, Eq, Ne,
// Lt, Lte, Gt, Gte, In, Nin, Exists, Prefix and Contains. The zero Filter
// matches everything and is omitted from requests.
type Filter struct {
	node map[string]any
	err  error
}

// IsZero reports whether f is the empty filter.
func (f Filter) IsZero() bool { return f.node == nil && f.err == nil }

// MarshalJSON encodes the filter grammar.
func (f Filter) MarshalJSON() ([]byte, error) {
	if f.err != nil {
		return nil, f.err
	}
	if f.node == nil {
		return []byte("null"), nil
	}
	return json.Marshal(f.node)
}

func combine(op string, fs []Filter) Filter {
	if len(fs) == 0 {
		return Filter{err: fmt.Errorf("terrabase: %s needs at least one filter", op)}
	}
	var errs error
	for _, f := range fs {
		if f.IsZero() {
			errs = errors.Join(errs, fmt.Errorf("terrabase: %s child is an empty filter", op))
		}
		errs = errors.Join(errs, f.err)
	}
	return Filter{node: map[string]any{op: fs}, err: errs}
}

// And matches when every child matches (1-64 children).
func And(fs ...Filter) Filter { return combine("and", fs) }

// Or matches when any child matches (1-64 children).
func Or(fs ...Filter) Filter { return combine("or", fs) }

// Not negates f.
func Not(f Filter) Filter {
	if f.IsZero() {
		return Filter{err: errors.New("terrabase: not of an empty filter")}
	}
	return Filter{node: map[string]any{"not": f}, err: f.err}
}

// scalar encodes a JSON scalar operand: string, exact number, bool or nil.
func scalar(v any) (json.RawMessage, error) {
	raw, err := json.Marshal(v)
	if err != nil {
		return nil, fmt.Errorf("terrabase: filter value: %w", err)
	}
	if t := bytes.TrimSpace(raw); len(t) > 0 && (t[0] == '{' || t[0] == '[') {
		return nil, fmt.Errorf("terrabase: filter value %s is not a JSON scalar", raw)
	}
	return raw, nil
}

func compare[P Path](op string, path P, value any) Filter {
	v, err := scalar(value)
	return Filter{node: map[string]any{op: []any{pathValue(path), v}}, err: err}
}

// Eq matches when the value at path equals value.
func Eq[P Path](path P, value any) Filter { return compare("eq", path, value) }

// Ne matches when the value at path differs from value or is missing.
func Ne[P Path](path P, value any) Filter { return compare("ne", path, value) }

// Lt matches values of value's type ordered before value.
func Lt[P Path](path P, value any) Filter { return compare("lt", path, value) }

// Lte matches values of value's type ordered before or equal to value.
func Lte[P Path](path P, value any) Filter { return compare("lte", path, value) }

// Gt matches values of value's type ordered after value.
func Gt[P Path](path P, value any) Filter { return compare("gt", path, value) }

// Gte matches values of value's type ordered after or equal to value.
func Gte[P Path](path P, value any) Filter { return compare("gte", path, value) }

// Contains matches arrays at path that contain the scalar value.
func Contains[P Path](path P, value any) Filter { return compare("contains", path, value) }

// Prefix matches strings at path that start with prefix.
func Prefix[P Path](path P, prefix string) Filter { return compare("prefix", path, prefix) }

func set[P Path](op string, path P, values []any) Filter {
	raws := make([]json.RawMessage, 0, len(values))
	var errs error
	if len(values) == 0 {
		errs = fmt.Errorf("terrabase: %s needs 1-256 values", op)
	}
	for _, v := range values {
		raw, err := scalar(v)
		errs = errors.Join(errs, err)
		raws = append(raws, raw)
	}
	return Filter{node: map[string]any{op: []any{pathValue(path), raws}}, err: errs}
}

// In matches when the value at path equals one of values (1-256).
func In[P Path](path P, values ...any) Filter { return set("in", path, values) }

// Nin matches when the value at path equals none of values (1-256).
func Nin[P Path](path P, values ...any) Filter { return set("nin", path, values) }

// Exists matches when the member at path is present, including null.
func Exists[P Path](path P) Filter {
	return Filter{node: map[string]any{"exists": pathValue(path)}}
}

// SortKey is one sort key.
type SortKey struct {
	path any
	desc bool
}

// Asc sorts by path ascending.
func Asc[P Path](path P) SortKey { return SortKey{path: pathValue(path)} }

// Desc sorts by path descending.
func Desc[P Path](path P) SortKey { return SortKey{path: pathValue(path), desc: true} }

// MarshalJSON encodes {"path":…,"order":"asc"|"desc"}.
func (k SortKey) MarshalJSON() ([]byte, error) {
	order := "asc"
	if k.desc {
		order = "desc"
	}
	return json.Marshal(struct {
		Path  any    `json:"path"`
		Order string `json:"order"`
	}{k.path, order})
}

// Query is a query-v1 request. Every field is optional.
type Query struct {
	Filter Filter `json:"filter,omitzero"`
	// Sort has at most 4 keys; results are finally ordered by $id ascending.
	Sort []SortKey `json:"sort,omitempty"`
	// Project keeps at most 64 paths (and their parent objects).
	Project []string `json:"project,omitempty"`
	// Limit is 1-1000; zero uses the server default of 50.
	Limit int `json:"limit,omitzero"`
	// Cursor continues from a previous page's NextCursor. It is bound to
	// Filter, Sort and Project; Limit may change between pages.
	Cursor string `json:"cursor,omitempty"`
	// Count requests the total match count; ignored when Cursor is set.
	Count bool `json:"count,omitzero"`
}

// Count is a match count; Value is a lower bound when Exact is false.
type Count struct {
	Value Uint64 `json:"value"`
	Exact bool   `json:"exact"`
}

// QueryResult is one page of query or list results.
type QueryResult struct {
	Items []Document `json:"items"`
	// NextCursor is empty after the last page. A page may be empty and still
	// have a NextCursor (bounded collection scans).
	NextCursor string `json:"-"`
	// Count is set when Query.Count was requested.
	Count *Count `json:"count,omitempty"`
}

// Query runs one page of q against the collection.
func (col *Collection) Query(ctx context.Context, q Query) (*QueryResult, error) {
	p, err := col.path("query")
	if err != nil {
		return nil, err
	}
	return col.db.c.page(ctx, request{method: http.MethodPost, path: p, replaySafe: true, readOnly: true}, q)
}

func (c *Client) page(ctx context.Context, req request, in any) (*QueryResult, error) {
	var out struct {
		QueryResult
		NextCursor *string `json:"next_cursor"`
	}
	if _, err := c.call(ctx, req, in, &out); err != nil {
		return nil, err
	}
	res := out.QueryResult
	if res.Items == nil {
		return nil, errors.New("terrabase: query response is missing an items array")
	}
	if out.NextCursor != nil {
		res.NextCursor = *out.NextCursor
	}
	return &res, nil
}

// Pages iterates over every page of q, starting at q.Cursor, until a page has
// no NextCursor. Iteration stops after the first error. Count is evaluated
// only without a cursor, so it is sent with the first page only.
func (col *Collection) Pages(ctx context.Context, q Query) iter.Seq2[*QueryResult, error] {
	return func(yield func(*QueryResult, error) bool) {
		q := q
		// Brent's cycle detector retains one cursor regardless of page count.
		anchor, power, length := q.Cursor, uint64(1), uint64(0)
		for {
			if err := ctx.Err(); err != nil {
				yield(nil, err)
				return
			}
			page, err := col.Query(ctx, q)
			if !yield(page, err) || err != nil || page.NextCursor == "" {
				return
			}
			length++
			if page.NextCursor == anchor {
				yield(nil, errors.New("terrabase: pagination cursor repeated"))
				return
			}
			if length == power {
				anchor, length = page.NextCursor, 0
				if power <= ^uint64(0)/2 {
					power *= 2
				}
			}
			q.Cursor, q.Count = page.NextCursor, false
		}
	}
}

// Items iterates over every matching document across pages.
func (col *Collection) Items(ctx context.Context, q Query) iter.Seq2[Document, error] {
	return func(yield func(Document, error) bool) {
		for page, err := range col.Pages(ctx, q) {
			if err != nil {
				yield(Document{}, err)
				return
			}
			for _, d := range page.Items {
				if err := ctx.Err(); err != nil {
					yield(Document{}, err)
					return
				}
				if !yield(d, nil) {
					return
				}
			}
		}
	}
}
