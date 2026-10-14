package v1

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"reflect"
	"slices"
	"strings"
	"testing"
)

const (
	applyTestShapeID = "a07ef7c2873b7b4c4fcbb88c9e682ca5"
	applyTestHandle  = "h1-f3171a2230109328"
	applyTestDocA    = "01ARZ3NDEKTSV4RRFFQ69G5FB0"
	applyTestDocB    = "01ARZ3NDEKTSV4RRFFQ69G5FB1"
	applyTestDocC    = "01ARZ3NDEKTSV4RRFFQ69G5FB9"
	// applyTestPageA is the base64url page key of applyTestDocA.
	applyTestPageA = "MDFBUlozTkRFS1RTVjRSUkZGUTY5RzVGQjA"
)

type applyVectorDoc struct {
	Version  string          `json:"version"`
	Document json.RawMessage `json:"document"`
}

type applyVectorFile struct {
	OffsetVectors []struct {
		Value string `json:"value"`
		Valid bool   `json:"valid"`
	} `json:"offset_vectors"`
	ApplyVectors []struct {
		ID             string                    `json:"id"`
		InitialState   map[string]applyVectorDoc `json:"initial_state"`
		Body           json.RawMessage           `json:"body"`
		ExpectedState  map[string]applyVectorDoc `json:"expected_state"`
		ExpectedOffset string                    `json:"expected_offset"`
	} `json:"apply_vectors"`
	HTTPVectors []struct {
		ID      string `json:"id"`
		Request struct {
			Query map[string]string `json:"query"`
		} `json:"request"`
		Response struct {
			Body json.RawMessage `json:"body"`
		} `json:"response"`
	} `json:"http_vectors"`
}

func loadApplyVectors(t *testing.T) applyVectorFile {
	t.Helper()
	data, err := os.ReadFile("testdata/shape-vectors.json")
	if err != nil {
		t.Fatal(err)
	}
	var vectors applyVectorFile
	if err := json.Unmarshal(data, &vectors); err != nil {
		t.Fatal(err)
	}
	return vectors
}

func applyTestCompact(t *testing.T, raw []byte) string {
	t.Helper()
	var buf bytes.Buffer
	if err := json.Compact(&buf, raw); err != nil {
		t.Fatal(err)
	}
	return buf.String()
}

func TestParseShapeOffsetVectors(t *testing.T) {
	for _, v := range loadApplyVectors(t).OffsetVectors {
		_, _, _, err := parseShapeOffset(v.Value)
		if (err == nil) != v.Valid {
			t.Errorf("offset %q: err %v, want valid %v", v.Value, err, v.Valid)
		}
	}
}

func TestParseShapeOffsetPages(t *testing.T) {
	snapshot, boundary, page, err := parseShapeOffset("snap.2." + applyTestPageA)
	if err != nil || !snapshot || boundary != 2 || page != applyTestDocA {
		t.Fatalf("snap page: got (%v, %d, %q, %v)", snapshot, boundary, page, err)
	}
	snapshot, boundary, page, err = parseShapeOffset("snap.0.0")
	if err != nil || !snapshot || boundary != 0 || page != "" {
		t.Fatalf("snap first page: got (%v, %d, %q, %v)", snapshot, boundary, page, err)
	}
	snapshot, boundary, _, err = parseShapeOffset("-1")
	if err != nil || snapshot || boundary != -1 {
		t.Fatalf("request -1: got (%v, %d, %v)", snapshot, boundary, err)
	}
	// The stdlib base64 decoder skips CR and LF; the re-encode check must reject them.
	for _, token := range []string{applyTestPageA + "\n", applyTestPageA[:10] + "\r\n" + applyTestPageA[10:], " " + applyTestPageA} {
		if _, _, _, err := parseShapeOffset("snap.2." + token); !errors.Is(err, errShapeInvalid) {
			t.Errorf("page token %q: err %v, want errShapeInvalid", token, err)
		}
	}
}

// applyTestState is the state after a bootstrap plus one chunk at offset 2.
func applyTestState() ShapeState {
	return ShapeState{
		Documents: map[string]Document{
			applyTestDocA: {ID: applyTestDocA, Version: "tbv1-b803db62170176ffadaa0ee88b8cb332", State: StateValue, Document: json.RawMessage(`{"title":"Hi <there>","read":false}`)},
			applyTestDocB: {ID: applyTestDocB, Version: "tbv1-86f0b3251c928096e66782e532821fed", State: StateValue, Document: json.RawMessage(`{"title":"Second","read":false}`)},
		},
		ShapeID:      applyTestShapeID,
		Handle:       applyTestHandle,
		Offset:       "2",
		CursorResume: "AQ",
	}
}

// applyTestBootstrap is the state before the first snapshot page.
func applyTestBootstrap() ShapeState {
	return ShapeState{Documents: map[string]Document{}, Offset: "-1"}
}

func applyTestUpsert(id, version, document string) string {
	return fmt.Sprintf(`{"op":"upsert","id":%q,"version":%q,"document":%s}`, id, version, document)
}

func applyTestDelete(id string) string {
	return fmt.Sprintf(`{"op":"delete","id":%q}`, id)
}

func applyTestChange(offset string, ops ...string) string {
	return fmt.Sprintf(`{"offset":%q,"ops":[%s]}`, offset, strings.Join(ops, ","))
}

// applyTestBody builds a body whose members are all present and valid.
func applyTestBody(offset, next, covered, values, upToDate string, changes ...string) string {
	return fmt.Sprintf(`{"shape_id":%q,"handle":%q,"offset":%q,"next_offset":%q,"covered_offset":%q,"values_at":%q,"up_to_date":%s,"cursor_resume":"AQ","changes":[%s]}`,
		applyTestShapeID, applyTestHandle, offset, next, covered, values, upToDate, strings.Join(changes, ","))
}

// applyTestChunkBody is a chunk at offset 2 that upserts A and deletes B.
func applyTestChunkBody() string {
	return applyTestBody("2", "4", "4", "4", "false", applyTestChange("4",
		applyTestUpsert(applyTestDocA, "tbv1-def52df94d5598d237cf1184a4f5cb00", `{"title":"Updated","read":true}`),
		applyTestDelete(applyTestDocB)))
}

// applyShapeBody is the test-only parse-then-apply path. It clones state, so the
// caller's value is never changed, including on failure.
func applyShapeBody(state ShapeState, data []byte, handle, offset string) (ShapeState, error) {
	wire, err := parseShapeBody(state, data, handle, offset)
	if err != nil {
		return state, err
	}
	return wire.apply(cloneShapeState(state)), nil
}

func TestApplyShapeBodyVectors(t *testing.T) {
	for _, v := range loadApplyVectors(t).ApplyVectors {
		t.Run(v.ID, func(t *testing.T) {
			var head struct {
				ShapeID string `json:"shape_id"`
				Handle  string `json:"handle"`
				Offset  string `json:"offset"`
			}
			if err := json.Unmarshal(v.Body, &head); err != nil {
				t.Fatal(err)
			}
			state := ShapeState{Documents: map[string]Document{}, ShapeID: head.ShapeID, Handle: head.Handle, Offset: head.Offset}
			for id, doc := range v.InitialState {
				state.Documents[id] = Document{ID: id, Version: doc.Version, State: StateValue, Document: doc.Document}
			}
			got, err := applyShapeBody(state, v.Body, head.Handle, head.Offset)
			if err != nil {
				t.Fatal(err)
			}
			if got.Offset != v.ExpectedOffset {
				t.Errorf("offset %q, want %q", got.Offset, v.ExpectedOffset)
			}
			if len(got.Documents) != len(v.ExpectedState) {
				t.Fatalf("documents %d, want %d", len(got.Documents), len(v.ExpectedState))
			}
			for id, want := range v.ExpectedState {
				doc, ok := got.Documents[id]
				if !ok || doc.Version != want.Version || applyTestCompact(t, doc.Document) != applyTestCompact(t, want.Document) {
					t.Errorf("document %s: got %+v, want %+v", id, doc, want)
				}
			}
		})
	}
}

func TestApplyShapeBodyHTTPVectors(t *testing.T) {
	// Bodies of the in-scope chunk and snapshot responses. Redirect-dependent vectors are not claimed.
	inScope := []string{"snapshot-first-page", "snapshot-last-page", "closed-commit-upsert-delete", "live-timeout"}
	for _, v := range loadApplyVectors(t).HTTPVectors {
		if !slices.Contains(inScope, v.ID) {
			continue
		}
		t.Run(v.ID, func(t *testing.T) {
			var body struct {
				NextOffset   string `json:"next_offset"`
				CursorResume string `json:"cursor_resume"`
			}
			if err := json.Unmarshal(v.Response.Body, &body); err != nil {
				t.Fatal(err)
			}
			state := applyTestBootstrap()
			if handle := v.Request.Query["handle"]; handle != "" {
				state = ShapeState{Documents: map[string]Document{}, Handle: handle, ShapeID: applyTestShapeID, Offset: v.Request.Query["offset"]}
			}
			got, err := applyShapeBody(state, v.Response.Body, v.Request.Query["handle"], v.Request.Query["offset"])
			if err != nil {
				t.Fatal(err)
			}
			if got.Offset != body.NextOffset {
				t.Errorf("offset %q, want %q", got.Offset, body.NextOffset)
			}
			wantCursor := body.CursorResume
			if strings.HasPrefix(body.NextOffset, "snap.") {
				wantCursor = ""
			}
			if got.CursorResume != wantCursor {
				t.Errorf("cursor %q, want %q", got.CursorResume, wantCursor)
			}
		})
	}
}

// TestApplyShapeBodyMalformedKeepsState checks that every rejected body returns the
// prior state unchanged, and that the prior state still applies a valid body afterwards.
func TestApplyShapeBodyMalformedKeepsState(t *testing.T) {
	valid := applyTestChunkBody()
	chunk := func(changes ...string) string {
		return applyTestBody("2", "4", "4", "4", "false", changes...)
	}
	upsertA := func(version string) string {
		return applyTestUpsert(applyTestDocA, version, `{"title":"Updated","read":true}`)
	}
	cases := []struct {
		name   string
		body   []byte
		offset string
	}{
		{"truncated", []byte(valid[:len(valid)-1]), "2"},
		{"trailing data", []byte(valid + " x"), "2"},
		{"second object", []byte(valid + valid), "2"},
		{"byte order mark", []byte("\uFEFF" + valid), "2"},
		{"invalid UTF-8 in document", []byte(strings.Replace(valid, "Updated", "Upd\xffated", 1)), "2"},
		{"oversized body", []byte(valid + strings.Repeat(" ", shapeBodyMax)), "2"},
		{"missing shape_id", []byte(strings.Replace(valid, `"shape_id":"`+applyTestShapeID+`",`, "", 1)), "2"},
		{"missing up_to_date", []byte(strings.Replace(valid, `"up_to_date":false,`, "", 1)), "2"},
		{"changes null", []byte(strings.Replace(valid, `"changes":[`, `"changes":null,"x":[`, 1)), "2"},
		{"offset is a number", []byte(strings.Replace(valid, `"offset":"2"`, `"offset":2`, 1)), "2"},
		{"body handle differs", []byte(strings.Replace(valid, applyTestHandle, "h1-0000000000000001", 1)), "2"},
		{"body offset differs", []byte(valid), "3"},
		{"uppercase shape_id", []byte(strings.Replace(valid, applyTestShapeID, "A07EF7C2873B7B4C4FCBB88C9E682CA5", 1)), "2"},
		{"empty cursor_resume", []byte(strings.Replace(valid, `"cursor_resume":"AQ"`, `"cursor_resume":""`, 1)), "2"},
		{"next offset does not advance", []byte(applyTestBody("2", "2", "4", "4", "false", applyTestChange("4", upsertA("tbv1-x")))), "2"},
		{"covered differs from next", []byte(applyTestBody("2", "4", "6", "6", "false", applyTestChange("4", upsertA("tbv1-x")))), "2"},
		{"values_at below covered", []byte(applyTestBody("2", "4", "4", "3", "false", applyTestChange("4", upsertA("tbv1-x")))), "2"},
		{"change at request offset", []byte(chunk(applyTestChange("2", upsertA("tbv1-x")))), "2"},
		{"change above covered", []byte(chunk(applyTestChange("5", upsertA("tbv1-x")))), "2"},
		{"chunk change without ops", []byte(chunk(`{"offset":"4","ops":[]}`)), "2"},
		{"ops not in id order", []byte(chunk(applyTestChange("4", applyTestDelete(applyTestDocB), upsertA("tbv1-x")))), "2"},
		{"duplicate id in change", []byte(chunk(applyTestChange("4", upsertA("tbv1-x"), applyTestDelete(applyTestDocA)))), "2"},
		{"invalid document key", []byte(chunk(applyTestChange("4", `{"op":"delete","id":"bad\u0001"}`))), "2"},
		{"upsert without version", []byte(chunk(applyTestChange("4", `{"op":"upsert","id":"`+applyTestDocA+`","document":{}}`))), "2"},
		{"upsert with empty version", []byte(chunk(applyTestChange("4", upsertA("")))), "2"},
		{"upsert document is an array", []byte(chunk(applyTestChange("4", applyTestUpsert(applyTestDocA, "tbv1-x", `[1]`)))), "2"},
		{"upsert document is null", []byte(chunk(applyTestChange("4", applyTestUpsert(applyTestDocA, "tbv1-x", `null`)))), "2"},
		{"delete with version", []byte(chunk(applyTestChange("4", `{"op":"delete","id":"`+applyTestDocA+`","version":"tbv1-x"}`))), "2"},
		{"delete with document", []byte(chunk(applyTestChange("4", `{"op":"delete","id":"`+applyTestDocA+`","document":{}}`))), "2"},
		{"unknown op", []byte(chunk(applyTestChange("4", `{"op":"merge","id":"`+applyTestDocA+`"}`))), "2"},
		{"chunk names a snapshot", []byte(applyTestBody("2", "snap.2."+applyTestPageA, "4", "4", "false", applyTestChange("4", upsertA("tbv1-x")))), "2"},
		{"request offset -1", []byte(applyTestBody("-1", "4", "4", "4", "false", applyTestChange("4", upsertA("tbv1-x")))), "-1"},
		{"same handle, other shape id", []byte(strings.Replace(valid, applyTestShapeID, "b07ef7c2873b7b4c4fcbb88c9e682ca5", 1)), "2"},
		{"snapshot first page up_to_date", []byte(applyTestBody("snap.2.0", "2", "2", "2", "true")), "snap.2.0"},
		{"snapshot change at other offset", []byte(applyTestBody("snap.2.0", "2", "2", "2", "false", applyTestChange("3", upsertA("tbv1-x")))), "snap.2.0"},
		{"snapshot two changes", []byte(applyTestBody("snap.2.0", "2", "2", "2", "false", applyTestChange("2", upsertA("tbv1-x")), applyTestChange("2"))), "snap.2.0"},
		{"snapshot delete op", []byte(applyTestBody("snap.2.0", "2", "2", "2", "false", applyTestChange("2", applyTestDelete(applyTestDocA)))), "snap.2.0"},
		{"snapshot next names another snapshot", []byte(applyTestBody("snap.2.0", "snap.3."+applyTestPageA, "2", "2", "false", applyTestChange("2", upsertA("tbv1-x")))), "snap.2.0"},
		{"snapshot next is first page", []byte(applyTestBody("snap.2.0", "snap.2.0", "2", "2", "false", applyTestChange("2", upsertA("tbv1-x")))), "snap.2.0"},
		{"snapshot op after its next key", []byte(applyTestBody("snap.2.0", "snap.2."+applyTestPageA, "2", "2", "false", applyTestChange("2", applyTestUpsert(applyTestDocB, "tbv1-x", `{}`)))), "snap.2.0"},
		{"snapshot page does not advance", []byte(applyTestBody("snap.2."+applyTestPageA, "snap.2."+applyTestPageA, "2", "2", "false", applyTestChange("2"))), "snap.2." + applyTestPageA},
		{"snapshot op at or before its page key", []byte(applyTestBody("snap.2."+applyTestPageA, "2", "2", "2", "false", applyTestChange("2", upsertA("tbv1-x")))), "snap.2." + applyTestPageA},
		{"zero progress without up_to_date", []byte(applyTestBody("2", "2", "2", "2", "false")), "2"},
		{"body member differs in case", []byte(strings.Replace(valid, `"handle":`, `"HANDLE":`, 1)), "2"},
		{"body member repeated", []byte(strings.Replace(valid, `"offset":"2"`, `"offset":"2","offset":"2"`, 1)), "2"},
		{"change member differs in case", []byte(strings.Replace(valid, `"offset":"4","ops"`, `"Offset":"4","ops"`, 1)), "2"},
		{"change member repeated", []byte(strings.Replace(valid, `"ops":[`, `"ops":[],"ops":[`, 1)), "2"},
		{"op member differs in case", []byte(strings.Replace(valid, `"op":"upsert"`, `"OP":"upsert"`, 1)), "2"},
		{"op id differs in case", []byte(strings.Replace(valid, `"id":"`+applyTestDocA, `"ID":"`+applyTestDocA, 1)), "2"},
		{"op member repeated", []byte(strings.Replace(valid, `"op":"upsert",`, `"op":"upsert","op":"upsert",`, 1)), "2"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			state := applyTestState()
			if strings.HasPrefix(c.offset, "snap.") {
				state = applyTestBootstrap()
				if c.offset != "snap.2.0" {
					state = ShapeState{Documents: applyTestState().Documents, ShapeID: applyTestShapeID, Handle: applyTestHandle, Offset: c.offset}
				}
			}
			before := cloneShapeState(state)
			got, err := applyShapeBody(state, c.body, applyTestHandle, c.offset)
			if !errors.Is(err, errShapeInvalid) {
				t.Fatalf("err %v, want errShapeInvalid", err)
			}
			if !reflect.DeepEqual(state, before) || !reflect.DeepEqual(got, before) {
				t.Fatalf("state changed on failure:\ngot  %+v\nwant %+v", got, before)
			}
			if state.Offset == "2" {
				if _, err := applyShapeBody(state, []byte(valid), applyTestHandle, "2"); err != nil {
					t.Fatalf("valid body after failure: %v", err)
				}
			}
		})
	}
}

func TestApplyShapeBodyAcceptsForwardCompatibleAndResumedBodies(t *testing.T) {
	t.Run("unknown members are ignored", func(t *testing.T) {
		body := strings.Replace(applyTestChunkBody(), `"up_to_date":false,`, `"up_to_date":false,"future":{"x":[1,2]},`, 1)
		if _, err := applyShapeBody(applyTestState(), []byte(body), applyTestHandle, "2"); err != nil {
			t.Fatal(err)
		}
	})
	t.Run("changed handle with a resume cursor", func(t *testing.T) {
		const newHandle = "h1-0123456789abcdef"
		const newShapeID = "b07ef7c2873b7b4c4fcbb88c9e682ca5"
		body := fmt.Sprintf(`{"shape_id":%q,"handle":%q,"offset":"1","next_offset":"4","covered_offset":"4","values_at":"4","up_to_date":false,"cursor_resume":"AQ","changes":[%s]}`,
			newShapeID, newHandle, applyTestChange("4", applyTestUpsert(applyTestDocA, "tbv1-def52df94d5598d237cf1184a4f5cb00", `{"title":"Updated","read":true}`)))
		got, err := applyShapeBody(applyTestState(), []byte(body), newHandle, "1")
		if err != nil {
			t.Fatal(err)
		}
		if got.Handle != newHandle || got.ShapeID != newShapeID || got.Offset != "4" {
			t.Errorf("state %+v", got)
		}
		if _, ok := got.Documents[applyTestDocB]; !ok {
			t.Errorf("document B dropped on resume")
		}
	})
	t.Run("exact document bytes are kept", func(t *testing.T) {
		raw := `{ "title" : "a  b" , "n" : 1.0 }`
		body := applyTestBody("2", "4", "4", "4", "false", applyTestChange("4", applyTestUpsert(applyTestDocA, "tbv1-x", raw)))
		got, err := applyShapeBody(applyTestState(), []byte(body), applyTestHandle, "2")
		if err != nil {
			t.Fatal(err)
		}
		if string(got.Documents[applyTestDocA].Document) != raw {
			t.Errorf("document bytes %q, want %q", got.Documents[applyTestDocA].Document, raw)
		}
	})
}

func TestApplyShapeBodySnapshotCursorHidden(t *testing.T) {
	t.Run("first page is not resumable", func(t *testing.T) {
		body := applyTestBody("snap.2.0", "snap.2."+applyTestPageA, "2", "2", "false", applyTestChange("2", upsertOnly(applyTestDocA)))
		got, err := applyShapeBody(applyTestBootstrap(), []byte(body), applyTestHandle, "snap.2.0")
		if err != nil {
			t.Fatal(err)
		}
		if got.CursorResume != "" || got.Offset != "snap.2."+applyTestPageA {
			t.Errorf("cursor %q offset %q", got.CursorResume, got.Offset)
		}
		if _, ok := got.Documents[applyTestDocB]; ok || len(got.Documents) != 1 {
			t.Errorf("first page kept documents from an earlier snapshot: %v", got.Documents)
		}
	})
	t.Run("final page publishes the cursor", func(t *testing.T) {
		state := ShapeState{Documents: map[string]Document{applyTestDocA: applyTestState().Documents[applyTestDocA]}, ShapeID: applyTestShapeID, Handle: applyTestHandle, Offset: "snap.2." + applyTestPageA}
		body := applyTestBody("snap.2."+applyTestPageA, "2", "2", "2", "false", applyTestChange("2", upsertOnly(applyTestDocB)))
		got, err := applyShapeBody(state, []byte(body), applyTestHandle, "snap.2."+applyTestPageA)
		if err != nil {
			t.Fatal(err)
		}
		if got.CursorResume != "AQ" || got.Offset != "2" || len(got.Documents) != 2 {
			t.Errorf("state %+v", got)
		}
	})
	t.Run("empty snapshot", func(t *testing.T) {
		body := applyTestBody("snap.2.0", "2", "2", "2", "false", applyTestChange("2"))
		got, err := applyShapeBody(applyTestBootstrap(), []byte(body), applyTestHandle, "snap.2.0")
		if err != nil {
			t.Fatal(err)
		}
		if len(got.Documents) != 0 || got.Offset != "2" || got.CursorResume != "AQ" {
			t.Errorf("state %+v", got)
		}
	})
}

func upsertOnly(id string) string {
	return applyTestUpsert(id, "tbv1-x", `{"title":"Hi","read":false}`)
}

func TestApplyShapeBodyOwnsItsState(t *testing.T) {
	state := applyTestState()
	before := cloneShapeState(state)
	got, err := applyShapeBody(state, []byte(applyTestChunkBody()), applyTestHandle, "2")
	if err != nil {
		t.Fatal(err)
	}
	got.Documents[applyTestDocA].Document[2] = 'X'
	if !reflect.DeepEqual(state, before) {
		t.Errorf("input state changed through the output:\ngot  %+v\nwant %+v", state, before)
	}
	clone := cloneShapeState(got)
	clone.Documents[applyTestDocA].Document[2] = 'Y'
	if got.Documents[applyTestDocA].Document[2] != 'X' {
		t.Errorf("clone shares document bytes with its source")
	}
}

func TestApplyShapeBodyFreshAndDocumentMembers(t *testing.T) {
	t.Run("live timeout at the head is fresh", func(t *testing.T) {
		body := applyTestBody("2", "2", "2", "2", "true")
		got, err := applyShapeBody(applyTestState(), []byte(body), applyTestHandle, "2")
		if err != nil {
			t.Fatal(err)
		}
		if got.Offset != "2" || !got.UpToDate || len(got.Documents) != 2 {
			t.Errorf("state %+v", got)
		}
	})
	t.Run("document member names keep their case", func(t *testing.T) {
		raw := `{"Title":"a","ID":1,"Op":"x","Offset":"9"}`
		body := applyTestBody("2", "4", "4", "4", "false", applyTestChange("4", applyTestUpsert(applyTestDocA, "tbv1-x", raw)))
		got, err := applyShapeBody(applyTestState(), []byte(body), applyTestHandle, "2")
		if err != nil {
			t.Fatal(err)
		}
		if string(got.Documents[applyTestDocA].Document) != raw {
			t.Errorf("document bytes %q, want %q", got.Documents[applyTestDocA].Document, raw)
		}
	})
}
