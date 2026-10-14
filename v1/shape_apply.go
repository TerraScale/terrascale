package v1

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"

	"terrascale.tech/sdk/internal/entityid"
)

// shapeBodyMax is the largest successful shape body the client accepts. It is
// checked before decoding.
const shapeBodyMax = 8 << 20

var errShapeInvalid = errors.New("terrabase: invalid shape response")

// shapeBodyWire is the exact body shape (shape-reads.md section 6). Pointers
// mark members that must be present, so absence is distinct from "".
type shapeBodyWire struct {
	ShapeID       *string           `json:"shape_id"`
	Handle        *string           `json:"handle"`
	Offset        *string           `json:"offset"`
	NextOffset    *string           `json:"next_offset"`
	CoveredOffset *string           `json:"covered_offset"`
	ValuesAt      *string           `json:"values_at"`
	UpToDate      *bool             `json:"up_to_date"`
	CursorResume  *string           `json:"cursor_resume"`
	Changes       []shapeChangeWire `json:"changes"`
}

type shapeChangeWire struct {
	Offset *string       `json:"offset"`
	Ops    []shapeOpWire `json:"ops"`
}

type shapeOpWire struct {
	Op       string          `json:"op"`
	ID       *string         `json:"id"`
	Version  *string         `json:"version"`
	Document json.RawMessage `json:"document"`
}

// applyShapeBody validates one whole body and returns the state it produces.
// The caller passes the handle and offset it requested, after any redirect; the
// body must echo them. A failed body returns state unchanged, so the caller
// keeps its checkpoint. A changed handle is accepted: a proven resume may move
// to another machine's handle and shape id.
func applyShapeBody(state ShapeState, data []byte, handle, offset string) (ShapeState, error) {
	if len(data) > shapeBodyMax {
		return state, badShape("body exceeds 8 MiB")
	}
	if !utf8.Valid(data) || bytes.HasPrefix(data, []byte("\uFEFF")) {
		return state, badShape("body is not UTF-8 without BOM")
	}
	var wire shapeBodyWire
	if err := decodeExact(data, &wire); err != nil {
		return state, badShape("body is not exact JSON")
	}
	required := []*string{wire.ShapeID, wire.Handle, wire.Offset, wire.NextOffset, wire.CoveredOffset, wire.ValuesAt, wire.CursorResume}
	if slices.Contains(required, nil) || wire.UpToDate == nil || wire.Changes == nil {
		return state, badShape("missing required member")
	}
	if *wire.CursorResume == "" {
		return state, badShape("empty cursor_resume")
	}
	if !isLowerHex(*wire.ShapeID, 32) || !isShapeHandle(*wire.Handle) {
		return state, badShape("shape id or handle")
	}
	if *wire.Handle != handle || *wire.Offset != offset {
		return state, badShape("body does not echo the request")
	}
	if handle == state.Handle && (state.ShapeID != *wire.ShapeID || state.Offset != offset) {
		return state, badShape("same handle with another shape or offset")
	}
	snapshot, boundary, page, err := parseShapeOffset(offset)
	if err != nil || boundary < 0 {
		return state, badShape("request offset")
	}
	nextSnap, next, nextPage, err := parseShapeOffset(*wire.NextOffset)
	if err != nil {
		return state, badShape("next offset")
	}
	covered, err := parseShapeDecimal(*wire.CoveredOffset)
	if err != nil {
		return state, badShape("covered offset")
	}
	values, err := parseShapeDecimal(*wire.ValuesAt)
	if err != nil || values < covered {
		return state, badShape("values_at")
	}
	if snapshot {
		// A page is one upsert change at S. Its progress is the last examined key.
		if *wire.UpToDate {
			return state, badShape("snapshot page reports up_to_date")
		}
		if covered != boundary || values != boundary || next != boundary || nextSnap != (nextPage != "") {
			return state, badShape("snapshot page offsets")
		}
		if nextSnap && page != "" && !shapeKeyLess(page, nextPage) {
			return state, badShape("snapshot page does not advance")
		}
		if len(wire.Changes) != 1 {
			return state, badShape("snapshot page must have one change")
		}
	} else if nextSnap || next < boundary || covered != next {
		return state, badShape("chunk offsets")
	}
	prev := boundary
	for _, change := range wire.Changes {
		if change.Offset == nil || change.Ops == nil {
			return state, badShape("change member")
		}
		off, err := parseShapeDecimal(*change.Offset)
		if err != nil {
			return state, badShape("change offset")
		}
		if snapshot && off != boundary || !snapshot && (off <= prev || off > covered || len(change.Ops) == 0) {
			return state, badShape("change offset range or empty ops")
		}
		prev = off
		if err := checkShapeOps(change.Ops, snapshot, page, nextSnap, nextPage); err != nil {
			return state, err
		}
	}
	out := cloneShapeState(state)
	if snapshot && page == "" {
		// The first page restarts the snapshot; earlier pages of another snapshot are discarded.
		out.Documents = map[string]Document{}
	}
	for _, change := range wire.Changes {
		for _, op := range change.Ops {
			if op.Op == "delete" {
				delete(out.Documents, *op.ID)
				continue
			}
			out.Documents[*op.ID] = Document{ID: *op.ID, Version: *op.Version, State: StateValue, Document: op.Document}
		}
	}
	out.ShapeID, out.Handle, out.Offset = *wire.ShapeID, *wire.Handle, *wire.NextOffset
	out.UpToDate = *wire.UpToDate
	out.CursorResume = *wire.CursorResume
	if nextSnap {
		// A snapshot cursor is usable only after its last page (section 7, rule 5).
		out.CursorResume = ""
	}
	return out, nil
}

// checkShapeOps validates one change's ops: ids are valid keys in strictly
// increasing order, and each op has the members its kind requires.
func checkShapeOps(ops []shapeOpWire, snapshot bool, page string, nextSnap bool, nextPage string) error {
	last := ""
	for _, op := range ops {
		if op.ID == nil || entityid.DocumentKey(*op.ID) != nil {
			return badShape("document id")
		}
		id := *op.ID
		if last != "" && !shapeKeyLess(last, id) {
			return badShape("ops are not in strictly increasing id order")
		}
		last = id
		if snapshot && (op.Op != "upsert" || (page != "" && !shapeKeyLess(page, id)) || (nextSnap && shapeKeyLess(nextPage, id))) {
			return badShape("snapshot op outside its page")
		}
		switch op.Op {
		case "upsert":
			if op.Version == nil || *op.Version == "" || len(op.Document) == 0 || op.Document[0] != '{' {
				return badShape("upsert needs version and object document")
			}
		case "delete":
			if op.Version != nil || op.Document != nil {
				return badShape("delete carries no version or document")
			}
		default:
			return badShape("unknown op")
		}
	}
	return nil
}

// parseShapeOffset parses a request or body offset (shape-reads.md section 3).
// The request offset -1 returns boundary -1. A snapshot offset returns its
// snapshot id as boundary, and its last examined key as page ("" for page 0).
func parseShapeOffset(raw string) (snapshot bool, boundary int64, page string, err error) {
	if raw == "-1" {
		return false, -1, "", nil
	}
	rest, found := strings.CutPrefix(raw, "snap.")
	if !found {
		boundary, err = parseShapeDecimal(raw)
		return false, boundary, "", err
	}
	id, token, ok := strings.Cut(rest, ".")
	if !ok {
		return false, 0, "", badShape("snapshot offset")
	}
	boundary, err = parseShapeDecimal(id)
	if err != nil {
		return false, 0, "", err
	}
	if token == "0" {
		return true, boundary, "", nil
	}
	page, err = decodeShapePage(token)
	if err != nil {
		return false, 0, "", err
	}
	return true, boundary, page, nil
}

// parseShapeDecimal accepts canonical unsigned decimals up to MaxInt64.
func parseShapeDecimal(s string) (int64, error) {
	if s == "" || len(s) > 1 && s[0] == '0' {
		return 0, badShape("decimal offset")
	}
	for i := range len(s) {
		if s[i] < '0' || s[i] > '9' {
			return 0, badShape("decimal offset")
		}
	}
	n, err := strconv.ParseInt(s, 10, 64)
	if err != nil {
		return 0, badShape("decimal offset out of range")
	}
	return n, nil
}

// decodeShapePage decodes a snapshot page key: canonical unpadded base64url of a
// valid document key. The re-encode check rejects inputs the stdlib decoder
// accepts, such as embedded CR or LF, and non-zero trailing bits.
func decodeShapePage(token string) (string, error) {
	raw, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil || base64.RawURLEncoding.EncodeToString(raw) != token || entityid.DocumentKey(string(raw)) != nil {
		return "", badShape("snapshot page key")
	}
	return string(raw), nil
}

// shapeKeyLess orders document keys by UTF-16 code unit, the ordinal order that
// DocumentKey counts in.
func shapeKeyLess(a, b string) bool {
	return slices.Compare(utf16.Encode([]rune(a)), utf16.Encode([]rune(b))) < 0
}

// isShapeHandle matches "h1-" and 16 lowercase hex digits.
func isShapeHandle(s string) bool {
	return strings.HasPrefix(s, "h1-") && isLowerHex(s[3:], 16)
}

func isLowerHex(s string, n int) bool {
	if len(s) != n {
		return false
	}
	for i := range n {
		if c := s[i]; (c < '0' || c > '9') && (c < 'a' || c > 'f') {
			return false
		}
	}
	return true
}

// cloneShapeState returns a state that shares no map or byte storage with state.
// Yielded states are cloned so each caller owns its copy.
func cloneShapeState(state ShapeState) ShapeState {
	out := state
	out.Documents = make(map[string]Document, len(state.Documents))
	for id, doc := range state.Documents {
		doc.Document = bytes.Clone(doc.Document)
		out.Documents[id] = doc
	}
	return out
}

// badShape keeps input values out of the message.
func badShape(reason string) error {
	return fmt.Errorf("%w: %s", errShapeInvalid, reason)
}
