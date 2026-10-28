package main

import (
	"bytes"
	"errors"
	"io"
	"strings"
	"testing"
)

type forbiddenInput struct{}

func (forbiddenInput) Read([]byte) (int, error) { panic("must not consume stdin") }

type brokenPrompt struct{}

func (brokenPrompt) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func TestDeleteConsent(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, input, project string
		yes, tty, ok         bool
	}{
		{"typed", "app\n", "project-id", false, true, true},
		{"crlf", "app\r\n", "", false, true, true},
		{"mismatch", "yes\n", "", false, true, false},
		{"spaces", " app \n", "", false, true, false},
		{"eof", "app", "", false, true, false},
		{"unattended", "", "", false, false, false},
		{"explicit", "", "", true, false, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			var in io.Reader = strings.NewReader(tc.input)
			if !tc.tty {
				in = forbiddenInput{}
			}
			var out bytes.Buffer
			err := confirmDelete("app", tc.project, tc.yes, in, &out, tc.tty)
			if (err == nil) != tc.ok {
				t.Fatalf("consent = %v", err)
			}
			if tc.tty && (!strings.Contains(out.String(), "app") || !strings.Contains(out.String(), "Project selector:") || !strings.Contains(out.String(), tc.project)) {
				t.Fatalf("missing context: %s", &out)
			}
			if !tc.tty && !tc.yes && !strings.Contains(err.Error(), "pass -yes") {
				t.Fatal(err)
			}
		})
	}
	if err := confirmDelete("app", "p", false, forbiddenInput{}, brokenPrompt{}, true); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatal(err)
	}
}

func TestDeleteGuardBeforeAuthentication(t *testing.T) {
	t.Parallel()
	err := runWithInput(t.Context(), []string{"db", "delete", "app"}, io.Discard, forbiddenInput{}, io.Discard, false)
	if err == nil || !strings.Contains(err.Error(), "pass -yes") {
		t.Fatalf("pre-auth guard: %v", err)
	}
	for _, args := range [][]string{{"-yes", "db", "list"}, {"-yes=false", "auth", "login"}, {"-yes", "completion", "bash"}, {"db", "list", "-yes"}} {
		if err := runWithInput(t.Context(), args, io.Discard, forbiddenInput{}, io.Discard, false); !errors.Is(err, errUsage) {
			t.Fatalf("run(%v) = %v", args, err)
		}
	}
}
