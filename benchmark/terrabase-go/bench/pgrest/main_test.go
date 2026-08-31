package main

import (
	"encoding/json"
	"net/http/httptest"
	"strings"
	"testing"
)

// The lane's whole point is real JSON work per request, so the smallest
// check that fails if the JSON handling breaks is a round-trip of the
// document shape through the exact encoder/decoder paths the handlers use.

func TestDocumentJSONRoundTripKeepsTheLaneContract(t *testing.T) {
	payload := strings.Repeat("x", 256)
	original := document{Name: "user-7", Group: 7, Payload: payload}

	stored, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	var decoded document
	if err := json.Unmarshal(stored, &decoded); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if decoded != original {
		t.Fatalf("round-trip changed the document: %+v != %+v", decoded, original)
	}

	// The stored body is what the database column holds verbatim; the
	// response adds the identifier the URL carried.
	response, err := json.Marshal(documentResponse{ID: "seed-7", Name: decoded.Name, Group: decoded.Group, Payload: decoded.Payload})
	if err != nil {
		t.Fatalf("marshal response: %v", err)
	}
	var value map[string]any
	if err := json.Unmarshal(response, &value); err != nil {
		t.Fatalf("unmarshal response: %v", err)
	}
	for _, field := range []string{"id", "name", "group", "payload"} {
		if _, ok := value[field]; !ok {
			t.Fatalf("response is missing field %q: %s", field, response)
		}
	}
	if len(value) != 4 {
		t.Fatalf("response has unexpected fields: %s", response)
	}
}

func TestPutRejectsAMalformedBody(t *testing.T) {
	// A PUT handler with a nil database never reaches the pool when the
	// body is malformed, so the JSON rejection path is testable alone.
	s := &service{}
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest("PUT", "/documents/benchmark-tenant/seed-1", strings.NewReader("{not json"))
	s.handlePut(recorder, request)
	if recorder.Code != 400 {
		t.Fatalf("malformed body must be a 400, got %d", recorder.Code)
	}
	if !strings.Contains(recorder.Body.String(), "error") {
		t.Fatalf("error response must name the error, got %q", recorder.Body.String())
	}
}

func TestPutRejectsADocumentWithoutAName(t *testing.T) {
	s := &service{}
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest("PUT", "/documents/benchmark-tenant/seed-1", strings.NewReader(`{"group":1,"payload":"x"}`))
	s.handlePut(recorder, request)
	if recorder.Code != 400 {
		t.Fatalf("nameless document must be a 400, got %d", recorder.Code)
	}
}

func TestParseLimitDefaultsAndCaps(t *testing.T) {
	cases := map[string]int{
		"":       1,
		"1":      1,
		"5":      5,
		"0":      1,
		"-3":     1,
		"nope":   1,
		"100000": 100,
	}
	for raw, want := range cases {
		if got := parseLimit(raw); got != want {
			t.Fatalf("parseLimit(%q) = %d, want %d", raw, got, want)
		}
	}
}
