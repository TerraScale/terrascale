package main

import (
	"bytes"
	"errors"
	"io"
	"slices"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestPagesFollowsNextAndStopsOnError(t *testing.T) {
	boom := errors.New("boom")
	var got []string
	for item, err := range pages(func(after string) (*v1.Page[string], error) {
		switch after {
		case "":
			return &v1.Page[string]{Items: []string{"a", "b"}, Next: "2"}, nil
		case "2":
			return &v1.Page[string]{Items: []string{"c"}, Next: "3"}, nil
		}
		return nil, boom
	}) {
		if err != nil {
			if !errors.Is(err, boom) {
				t.Fatal(err)
			}
			break
		}
		got = append(got, item)
	}
	if !slices.Equal(got, []string{"a", "b", "c"}) {
		t.Fatalf("pages = %v", got)
	}
}

func TestRunRejectsUnknownCommandsOffline(t *testing.T) {
	for _, args := range [][]string{nil, {"auth"}, {"db"}, {"-nope"}, {"version", "extra"}, {"--version", "extra"}} {
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
