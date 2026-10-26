package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"testing"
)

func TestScopedHelpIsOfflineAndRejectsUnknownCommands(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		args []string
		want string
		bad  bool
	}{
		{nil, "usage: ts", false},
		{[]string{"help", "db"}, "ts db create <database>", false},
		{[]string{"db", "create", "--help"}, "ts db create <database>", false},
		{[]string{"-project", "private", "doc", "put", "--help"}, "ts doc put", false},
		{[]string{"upgrade", "--help"}, "", true},
		{[]string{"auth", "invalid", "--help"}, "", true},
	} {
		var out bytes.Buffer
		handled, err := requestedHelp(tc.args, &out)
		if !handled || errors.Is(err, errUsage) != tc.bad || (err == nil && !strings.Contains(out.String(), tc.want)) || strings.Contains(out.String(), "private") {
			t.Fatalf("help(%v) = %v, %v, %q", tc.args, handled, err, &out)
		}
		if len(tc.args) > 2 && strings.Contains(out.String(), "migrate up") {
			t.Fatal("scoped help included unrelated commands")
		}
	}
	var out bytes.Buffer
	if _, err := requestedHelp([]string{"keys", "create", "--help"}, &jsonOutput{Writer: &out}); err != nil || !json.Valid(out.Bytes()) {
		t.Fatalf("JSON help = %v, %q", err, &out)
	}
	if _, err := requestedHelp([]string{"help"}, brokenPrompt{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatal(err)
	}
}

func TestParserErrorsKeepReasonsWithoutValues(t *testing.T) {
	t.Parallel()
	for _, message := range []string{"flag provided but not defined: -private", "flag needs an argument: -private", "invalid value private for flag -limit"} {
		err := parserError(errors.New(message))
		if !errors.Is(err, errUsage) || strings.Contains(err.Error(), "private") || !strings.Contains(err.Error(), "ts help") {
			t.Fatal(err)
		}
	}
}
