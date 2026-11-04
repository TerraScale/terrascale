package main

import (
	"bytes"

	"errors"

	"io"

	"strings"

	"testing"
)

func TestRunRejectsUnknownCommandsOffline(t *testing.T) {
	// Operand counts are checked before sign-in, so none of these reaches the network.
	for _, args := range [][]string{{"auth"}, {"db"}, {"-nope"}, {"version", "extra"}, {"--version", "extra"},
		{"db", "list", "extra"}, {"db", "create"}, {"sql", "app"}, {"sql", "app", "SELECT 1", "x"},
		{"keys", "create"}, {"whoami", "x"}, {"frob"}, {"-project", "p"},
		{"backup", "restore", "app", "id"}, {"projects", "create"}, {"usage", "a", "b"}, {"completion"},
		{"databases", "rename", "app"}, {"db", "rename"}, {"db", "rename", "app"}, {"db", "rename", "app", "new", "extra"},
		{"backup", "restore-at", "app", "2026-10-23T00:00:00Z"}, {"-home-region", "gru-1", "db", "list"},
		{"db", "placement", "app"}, {"db", "placement", "app", "-replica-regions", "sjc", "-all-regions"}, {"db", "move-home", "app", "Bad!"},
		{"functions", "list"}, {"functions", "invoke", "app"}, {"functions", "logs", "app", "fn", "-limit", "101"},
		{"functions", "delete", "app", "fn", "-data", "{}"}} {
		if err := run(t.Context(), args, io.Discard); !errors.Is(err, errUsage) {
			t.Errorf("run(%q) = %v, want usage", args, err)
		}
	}
}

func TestVersionOffline(t *testing.T) {
	for _, arg := range []string{"version", "--version"} {
		var out bytes.Buffer
		if err := run(t.Context(), []string{arg}, &out); err != nil {
			t.Fatal(err)
		}
		if got, want := out.String(), "ts "+version+"\n"; got != want {
			t.Fatalf("version = %q, want %q", got, want)
		}
	}
}

func TestHelpOffline(t *testing.T) {
	for _, args := range [][]string{nil, {"help"}, {"-h"}, {"--help"}} {
		var out bytes.Buffer
		if err := run(t.Context(), args, &out); err != nil {
			t.Fatalf("run(%q) = %v", args, err)
		}
		if out.String() != usage {
			t.Fatalf("run(%q) printed %q", args, out.String())
		}
	}
	for _, tc := range []struct {
		args []string
		want string
	}{{[]string{"db", "--help"}, "ts db create <database>"}, {[]string{"-project", "p", "sql", "-help"}, "ts sql <database>"}} {
		var out bytes.Buffer
		if err := run(t.Context(), tc.args, &out); err != nil || !strings.Contains(out.String(), tc.want) || strings.Contains(out.String(), "migrate up") {
			t.Fatalf("scoped help(%q) = %q, %v", tc.args, out.String(), err)
		}
	}
}
