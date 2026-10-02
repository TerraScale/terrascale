package v1test

// Contract fixture: query-v1 filter grammar, sort, projection, keyset cursors,
// counts and explain. See fixture_test.go.

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math/big"
	"net/http"
	"slices"
	"strconv"
	"strings"
)

type fxPath struct {
	pointer string
	lower   bool
	id      bool
}

func parsePath(v any) (fxPath, error) {
	switch p := v.(type) {
	case string:
		if p == "$id" {
			return fxPath{id: true}, nil
		}
		if n := len(splitPointer(p)); n < 1 || n > 16 {
			return fxPath{}, fmt.Errorf("bad path %q", p)
		}
		return fxPath{pointer: p}, nil
	case map[string]any:
		inner, ok := p["lower"].(string)
		if len(p) != 1 || !ok {
			return fxPath{}, fmt.Errorf("bad path object")
		}
		fp, err := parsePath(inner)
		fp.lower = true
		return fp, err
	}
	return fxPath{}, fmt.Errorf("bad path %v", v)
}

func (p fxPath) get(id string, obj map[string]any) (any, bool) {
	if p.id {
		return id, true
	}
	v, ok := lookup(obj, p.pointer)
	if s, isStr := v.(string); ok && isStr && p.lower {
		v = strings.ToLower(s)
	}
	return v, ok
}

type fxFilter struct {
	op       string
	children []*fxFilter
	path     fxPath
	value    any
	values   []any
}

func (f *fxFilter) match(id string, obj map[string]any) bool {
	if f == nil {
		return true
	}
	switch f.op {
	case "and":
		for _, c := range f.children {
			if !c.match(id, obj) {
				return false
			}
		}
		return true
	case "or":
		for _, c := range f.children {
			if c.match(id, obj) {
				return true
			}
		}
		return false
	case "not":
		return !f.children[0].match(id, obj)
	}
	v, present := f.path.get(id, obj)
	switch f.op {
	case "exists":
		return present
	case "eq":
		return present && scalarOK(v) && compareValues(v, f.value) == 0
	case "ne":
		return !present || !scalarOK(v) || compareValues(v, f.value) != 0
	case "lt", "lte", "gt", "gte":
		if !present || typeClass(v) != typeClass(f.value) {
			return false
		}
		c := compareValues(v, f.value)
		return map[string]bool{"lt": c < 0, "lte": c <= 0, "gt": c > 0, "gte": c >= 0}[f.op]
	case "in", "nin":
		found := false
		if present && scalarOK(v) {
			for _, candidate := range f.values {
				if compareValues(v, candidate) == 0 {
					found = true
				}
			}
		}
		return found == (f.op == "in")
	case "prefix":
		s, ok := v.(string)
		return present && ok && strings.HasPrefix(s, f.value.(string))
	case "contains":
		arr, ok := v.([]any)
		if !present || !ok {
			return false
		}
		return slices.ContainsFunc(arr, func(e any) bool { return scalarOK(e) && compareValues(e, f.value) == 0 })
	}
	return false
}

// typeClass is the comparison type: ordered operators compare only within it.
func typeClass(v any) int {
	if r := rank(v); r != 3 {
		return r
	}
	return 2 // true and false are both booleans
}

func scalarOK(v any) bool {
	switch v.(type) {
	case []any, map[string]any:
		return false
	}
	return true
}

// rank orders types: missing < null < false < true < numbers < strings < arrays/objects.
func rank(v any) int {
	switch x := v.(type) {
	case nil:
		return 1
	case bool:
		if x {
			return 3
		}
		return 2
	case json.Number:
		return 4
	case string:
		return 5
	}
	return 6
}

func compareValues(a, b any) int {
	ra, rb := rank(a), rank(b)
	if ra != rb {
		return ra - rb
	}
	switch x := a.(type) {
	case json.Number:
		xa, _ := new(big.Rat).SetString(x.String())
		xb, _ := new(big.Rat).SetString(b.(json.Number).String())
		return xa.Cmp(xb)
	case string:
		return strings.Compare(x, b.(string))
	}
	return 0
}

func parseFilter(raw json.RawMessage) (*fxFilter, error) {
	if len(raw) == 0 || string(raw) == "null" {
		return nil, nil
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	var v any
	if err := dec.Decode(&v); err != nil {
		return nil, err
	}
	return buildFilter(v, 0)
}

func buildFilter(v any, depth int) (*fxFilter, error) {
	m, ok := v.(map[string]any)
	if !ok || len(m) != 1 || depth > 8 {
		return nil, fmt.Errorf("bad filter node")
	}
	for op, arg := range m {
		f := &fxFilter{op: op}
		switch op {
		case "and", "or":
			list, ok := arg.([]any)
			if !ok || len(list) < 1 || len(list) > 64 {
				return nil, fmt.Errorf("bad %s", op)
			}
			for _, c := range list {
				child, err := buildFilter(c, depth+1)
				if err != nil {
					return nil, err
				}
				f.children = append(f.children, child)
			}
		case "not":
			child, err := buildFilter(arg, depth+1)
			if err != nil {
				return nil, err
			}
			f.children = []*fxFilter{child}
		case "exists":
			p, err := parsePath(arg)
			if err != nil {
				return nil, err
			}
			f.path = p
		case "eq", "ne", "lt", "lte", "gt", "gte", "prefix", "contains", "in", "nin":
			pair, ok := arg.([]any)
			if !ok || len(pair) != 2 {
				return nil, fmt.Errorf("bad %s operands", op)
			}
			p, err := parsePath(pair[0])
			if err != nil {
				return nil, err
			}
			f.path = p
			if op == "in" || op == "nin" {
				vals, ok := pair[1].([]any)
				if !ok || len(vals) < 1 || len(vals) > 256 {
					return nil, fmt.Errorf("bad %s values", op)
				}
				for _, v := range vals {
					if !scalarOK(v) {
						return nil, fmt.Errorf("non-scalar")
					}
				}
				f.values = vals
				break
			}
			if !scalarOK(pair[1]) {
				return nil, fmt.Errorf("non-scalar")
			}
			if _, isStr := pair[1].(string); op == "prefix" && !isStr {
				return nil, fmt.Errorf("prefix needs a string")
			}
			if (op == "lt" || op == "lte" || op == "gt" || op == "gte") && pair[1] == nil {
				return nil, fmt.Errorf("ordered comparison with null")
			}
			f.value = pair[1]
		default:
			return nil, fmt.Errorf("unknown operator %q", op)
		}
		if f.path.lower {
			f.value = lowered(f.value)
			for i, v := range f.values {
				f.values[i] = lowered(v)
			}
		}
		return f, nil
	}
	return nil, fmt.Errorf("empty filter")
}

type fxSortKey struct {
	Path  any    `json:"path"`
	Order string `json:"order"`
}

type fxQuery struct {
	Filter  json.RawMessage `json:"filter"`
	Sort    []fxSortKey     `json:"sort"`
	Project []string        `json:"project"`
	Limit   *int            `json:"limit"`
	Cursor  *string         `json:"cursor"`
	Count   bool            `json:"count"`
	Explain string          `json:"explain"`
}

type fxCursor struct {
	Digest string            `json:"d"`
	Keys   []json.RawMessage `json:"k"` // null for missing is encoded as {"m":1}
	ID     string            `json:"id"`
}

type fxRow struct {
	id   string
	doc  *fxDoc
	obj  map[string]any
	keys []any // sortMissing for absent
}

type fxMissing struct{}

func sortCompare(a, b []any, desc []bool) int {
	for i := range a {
		_, am := a[i].(fxMissing)
		_, bm := b[i].(fxMissing)
		c := 0
		switch {
		case am && bm:
		case am:
			c = -1
		case bm:
			c = 1
		default:
			c = compareValues(a[i], b[i])
		}
		if desc[i] {
			c = -c
		}
		if c != 0 {
			return c
		}
	}
	return 0
}

func (f *fixture) query(r *http.Request, k *fxKey, body []byte) fxReply {
	_, coll, rep := f.scope(k, r.PathValue("db"), r.PathValue("coll"), "read")
	if rep != nil {
		return *rep
	}
	var q fxQuery
	if err := strictDecode(body, &q); err != nil {
		return fxErr(400, "invalid_query", "message", err.Error())
	}
	limit := 50
	if q.Limit != nil {
		limit = *q.Limit
	}
	if limit < 1 || limit > 1000 || len(q.Sort) > 4 || len(q.Project) > 64 || (q.Explain != "" && q.Explain != "plan" && q.Explain != "analyze") {
		return fxErr(400, "invalid_query")
	}
	filter, err := parseFilter(q.Filter)
	if err != nil {
		return fxErr(400, "invalid_query", "message", err.Error())
	}
	paths := make([]fxPath, len(q.Sort))
	desc := make([]bool, len(q.Sort)+1)
	for i, s := range q.Sort {
		if paths[i], err = parsePath(s.Path); err != nil || (s.Order != "asc" && s.Order != "desc") {
			return fxErr(400, "invalid_query")
		}
		desc[i] = s.Order == "desc"
	}
	// Cursors bind filter, sort and project; limit may vary, and count and
	// explain are evaluated only without a cursor.
	digestSrc, _ := json.Marshal([]any{coll.name, q.Filter, q.Sort, q.Project})
	if q.Cursor != nil {
		q.Count, q.Explain = false, ""
	}
	sum := sha256.Sum256(digestSrc)
	digest := hex.EncodeToString(sum[:8])

	var rows []fxRow
	for id, d := range coll.docs {
		if !f.visible(coll, d) {
			continue
		}
		objs := []map[string]any{mustObject(d.raw)}
		if d.siblings != nil {
			objs = objs[:0]
			for _, s := range d.siblings {
				if !s.deleted {
					objs = append(objs, mustObject(s.raw))
				}
			}
		}
		matched := slices.ContainsFunc(objs, func(o map[string]any) bool { return filter.match(id, o) })
		if !matched {
			continue
		}
		row := fxRow{id: id, doc: d, obj: objs[0]}
		for _, p := range paths {
			v, ok := p.get(id, row.obj)
			if !ok {
				row.keys = append(row.keys, fxMissing{})
			} else {
				row.keys = append(row.keys, v)
			}
		}
		row.keys = append(row.keys, id)
		rows = append(rows, row)
	}
	slices.SortFunc(rows, func(a, b fxRow) int { return sortCompare(a.keys, b.keys, desc) })
	total := len(rows)

	start := 0
	if q.Cursor != nil {
		raw, err := base64.RawURLEncoding.DecodeString(*q.Cursor)
		var c fxCursor
		if err != nil || json.Unmarshal(raw, &c) != nil || c.Digest != digest || len(c.Keys) != len(paths) {
			return fxErr(400, "invalid_cursor")
		}
		pos := make([]any, 0, len(paths)+1)
		for _, kraw := range c.Keys {
			if string(kraw) == `{"m":1}` {
				pos = append(pos, fxMissing{})
				continue
			}
			dec := json.NewDecoder(bytes.NewReader(kraw))
			dec.UseNumber()
			var v any
			dec.Decode(&v)
			pos = append(pos, v)
		}
		pos = append(pos, c.ID)
		start = len(rows)
		for i, row := range rows {
			if sortCompare(row.keys, pos, desc) > 0 {
				start = i
				break
			}
		}
	}
	out := map[string]any{"items": []any{}, "next_cursor": nil}
	if q.Count {
		out["count"] = map[string]any{"value": strconv.Itoa(total), "exact": true}
	}
	if q.Explain != "" {
		explain := map[string]any{"plan": map[string]any{"kind": "collection_scan", "residual": filter != nil, "sort": "memory"}, "estimated_rows": strconv.Itoa(total)}
		if q.Explain == "plan" {
			out["explain"] = explain
			return fxOK(200, out)
		}
		explain["actual"] = map[string]any{"rows": strconv.Itoa(min(limit, len(rows)-start)), "documents_read": strconv.Itoa(len(coll.docs))}
		out["explain"] = explain
	}
	end := min(start+limit, len(rows))
	items := make([]any, 0, end-start)
	for _, row := range rows[start:end] {
		item := docJSON(row.id, row.doc)
		if len(q.Project) > 0 && row.doc.siblings == nil {
			item["document"] = project(row.obj, q.Project)
		}
		items = append(items, item)
	}
	out["items"] = items
	if end < len(rows) {
		last := rows[end-1]
		c := fxCursor{Digest: digest, ID: last.id}
		for _, kv := range last.keys[:len(paths)] {
			if _, m := kv.(fxMissing); m {
				c.Keys = append(c.Keys, json.RawMessage(`{"m":1}`))
			} else {
				c.Keys = append(c.Keys, mustRaw(kv))
			}
		}
		out["next_cursor"] = base64.RawURLEncoding.EncodeToString(mustRaw(c))
	}
	return fxOK(200, out)
}

// project keeps only the listed members with their parent objects.
func project(obj map[string]any, paths []string) map[string]any {
	out := map[string]any{}
	for _, p := range paths {
		v, ok := lookup(obj, p)
		if !ok {
			continue
		}
		dst, last, ok := parent(out, p, true)
		if ok {
			dst[last] = v
		}
	}
	return out
}

func lowered(v any) any {
	if s, ok := v.(string); ok {
		return strings.ToLower(s)
	}
	return v
}
