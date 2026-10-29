package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"testing"
)

func TestJSONStreamTerminals(t *testing.T) {
	t.Parallel()
	for _, fail := range []bool{false, true} {
		var buf bytes.Buffer
		out := &jsonOutput{Writer: &buf}
		beginJSONStream(out)
		if err := emitJSONRecord(out, json.RawMessage(`{"n":9007199254740993}`)); err != nil {
			t.Fatal(err)
		}
		if fail {
			if err := emitJSONError(out, context.Canceled); err != nil {
				t.Fatal(err)
			}
		} else if err := finishJSONStream(out); err != nil {
			t.Fatal(err)
		}
		if err := finishJSONStream(out); err != nil {
			t.Fatal(err)
		}
		lines := strings.Split(strings.TrimSpace(buf.String()), "\n")
		if len(lines) != 2 || !json.Valid([]byte(lines[0])) || !strings.Contains(lines[0], `"type":"record"`) || !strings.Contains(lines[0], "9007199254740993") {
			t.Fatal("invalid record stream")
		}
		want := `"type":"complete"`
		if fail {
			want = `"type":"error"`
		}
		if !strings.Contains(lines[1], want) {
			t.Fatal("missing terminal record")
		}
	}
	var buf bytes.Buffer
	out := &jsonOutput{Writer: &buf}
	beginJSONStream(out)
	if err := finishJSONStream(out); err != nil || buf.String() != "{\"type\":\"complete\"}\n" {
		t.Fatal("empty stream must complete")
	}
	broken := &jsonOutput{Writer: brokenPrompt{}}
	beginJSONStream(broken)
	if err := emitJSONRecord(broken, 1); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatal(err)
	}
	if !broken.failed || finishJSONStream(broken) != nil {
		t.Fatal("writer failure cannot become complete")
	}
}
