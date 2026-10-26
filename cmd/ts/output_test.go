package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

func TestJSONOfflineContract(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		args []string
		code int
	}{
		{[]string{"--json", "--version"}, 0},
		{[]string{"version", "--json"}, 0},
		{[]string{"help", "--json"}, 0},
		{[]string{"--json", "--unknown"}, 2},
		{[]string{"db", "create", "--json"}, 2},
		{[]string{"--json=invalid", "version"}, 2},
		{[]string{"whoami", "--json"}, 1},              // Not implemented in this slice; never emits prose.
		{[]string{"db", "delete", "app", "--json"}, 1}, // Consent precedes authentication.
	} {
		var out bytes.Buffer
		err := runWithInput(t.Context(), tc.args, &out, forbiddenInput{}, io.Discard, false)
		if exitStatus(err) != tc.code || !json.Valid(out.Bytes()) {
			t.Fatalf("run(%v) = %v, %q", tc.args, err, &out)
		}
	}
	var out bytes.Buffer
	if err := run(t.Context(), []string{"--json=false", "version"}, &out); err != nil || out.String() != "ts "+version+"\n" {
		t.Fatalf("text mode = %v, %q", err, &out)
	}
	rest, mode, err := jsonArguments([]string{"functions", "invoke", "app", "f", "-data", "--json"})
	if err != nil || mode || !slices.Equal(rest, []string{"functions", "invoke", "app", "f", "-data", "--json"}) {
		t.Fatalf("flag value changed: %v, %v, %v", rest, mode, err)
	}
}

func TestJSONErrorsExposeOnlySafeMetadata(t *testing.T) {
	t.Parallel()
	for _, err := range []error{
		&v1.APIError{Status: 409, Code: v1.ErrWriteOutcomeUnknown, RequestID: "request_123", Message: "private", Body: json.RawMessage(`{"token":"private"}`)},
		&tsauth.APIError{Status: 403, Code: "forbidden", Message: "private"},
		&tsauth.Error{Status: 401, Code: "invalid_client", Description: "private"},
		errors.New("private"),
	} {
		var out bytes.Buffer
		if e := emitJSONError(&out, err); e != nil || !json.Valid(out.Bytes()) || strings.Contains(out.String(), "private") {
			t.Fatalf("error output = %q, %v", &out, e)
		}
		var envelope struct{ Error commandError }
		if e := json.Unmarshal(out.Bytes(), &envelope); e != nil || envelope.Error.Code == "" || envelope.Error.Message == "" {
			t.Fatalf("missing error metadata: %q, %v", &out, e)
		}
		if errors.Is(err, v1.ErrCommitUncertain) && (!strings.Contains(out.String(), `"requestId":"request_123"`) || !strings.Contains(out.String(), "before retrying")) {
			t.Fatal(out.String())
		}
	}
	if safeIdentifier("bad\nheader") != "" {
		t.Fatal("unsafe request ID accepted")
	}
	if err := run(t.Context(), []string{"--json", "version"}, brokenPrompt{}); !errors.Is(err, io.ErrClosedPipe) || exitStatus(err) != 1 {
		t.Fatalf("writer failure = %v", err)
	}
}

func TestJSONDatabaseResults(t *testing.T) {
	t.Parallel()
	const project = "01M5N8CG630T6P449ES15GNZ8N"
	const item = `{"id":"01M5N9000000000000000000AA","projectId":"` + project + `","name":"app"}`
	for _, tc := range []struct {
		name, body, prefix string
		args               []string
	}{
		{"db list", `{"items":[],"next":""}`, "[]", nil},
		{"db list", `{"items":[` + item + `],"next":""}`, "[{", nil},
		{"db list", `{"items":[` + item + `,` + item + `],"next":""}`, "[{", nil},
		{"db list", `{"items":[` + item + `],"next":"page2"}`, "[{", nil},
		{"db info", item, "{", []string{"app"}},
		{"db create", item, "{", []string{"app"}},
		{"db branch", item, "{", []string{"app", "copy"}},
		{"db regions", item, "{", []string{"app", "none"}},
		{"db delete", item, `{"name":"app","deleted":true}`, []string{"app"}},
	} {
		t.Run(tc.name+tc.body, func(t *testing.T) {
			t.Parallel()
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				if r.Method == http.MethodDelete {
					w.WriteHeader(http.StatusNoContent)
				} else if r.URL.Query().Get("after") != "" {
					io.WriteString(w, `{"items":[],"next":""}`)
				} else {
					io.WriteString(w, tc.body)
				}
			}))
			defer srv.Close()
			c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
			if err != nil {
				t.Fatal(err)
			}
			var out bytes.Buffer
			s := session{tb: c, project: project}
			if err := s.run(t.Context(), tc.name, tc.args, &jsonOutput{Writer: &out}); err != nil || !json.Valid(out.Bytes()) || !strings.HasPrefix(out.String(), tc.prefix) {
				t.Fatalf("result = %q, %v", &out, err)
			}
			if tc.name == "db list" {
				var items []v1.ManagedDatabase
				if err := json.Unmarshal(out.Bytes(), &items); err != nil || len(items) != strings.Count(tc.body, `"id"`) {
					t.Fatalf("list = %q, %v", &out, err)
				}
			} else if !strings.Contains(out.String(), `"name":"app"`) {
				t.Fatalf("database metadata missing: %q", &out)
			}
		})
	}
}
