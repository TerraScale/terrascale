package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"flag"
	"io"
	"net/http"
	"slices"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestOptionsAfterCommandAndOperands(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		args, want  []string
		org, cursor string
		yes         bool
	}{
		{args: []string{"projects", "info", "p", "-org", "o"}, want: []string{"projects", "info", "p"}, org: "o"},
		{args: []string{"db", "changes", "app", "people", "-cursor", "empty"}, want: []string{"db", "changes", "app", "people"}, cursor: "empty"},
		{args: []string{"keys", "-yes", "revoke", "id"}, want: []string{"keys", "revoke", "id"}, yes: true},
		{args: []string{"doc", "put", "app", "c", "-"}, want: []string{"doc", "put", "app", "c", "-"}},
		{args: []string{"sql", "app", "-x", "--org=o"}, want: []string{"sql", "app", "-x"}, org: "o"},
		{args: []string{"doc", "get", "app", "c", "--", "-org"}, want: []string{"doc", "get", "app", "c", "-org"}},
		{args: []string{"-org", "o", "--", "-yes"}, want: []string{"-yes"}, org: "o"},
	} {
		fs := flag.NewFlagSet("ts", flag.ContinueOnError)
		fs.SetOutput(io.Discard)
		org, cursor, yes := fs.String("org", "", ""), fs.String("cursor", "", ""), fs.Bool("yes", false, "")
		got, err := parseOptions(fs, tc.args)
		if err != nil || !slices.Equal(got, tc.want) || *org != tc.org || *cursor != tc.cursor || *yes != tc.yes {
			t.Errorf("parseOptions(%q) = %q, %v, org=%q cursor=%q yes=%v", tc.args, got, err, *org, *cursor, *yes)
		}
	}
}

func TestLocalValidationBeforeAuthentication(t *testing.T) {
	t.Parallel()
	const id = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
	for _, tc := range []struct {
		args []string
		code int
		want string
	}{
		{[]string{"keys", "revoke", "nope"}, 2, "canonical ULID"},
		{[]string{"keys", "revoke", id}, 1, "pass -yes"},
		{[]string{"functions", "logs", "app", "hello", "-limit", "0"}, 2, "-limit must be from 1 to 100"},
		{[]string{"functions", "logs", "app", "hello", "-limit", "101"}, 2, "-limit must be from 1 to 100"},
		{[]string{"db", "delete", "app"}, 1, "pass -yes"},
	} {
		err := runWithInput(t.Context(), tc.args, io.Discard, forbiddenInput{}, io.Discard, false)
		if exitStatus(err) != tc.code || err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("run(%q) = %v, want exit %d with %q", tc.args, err, tc.code, tc.want)
		}
	}
}

func TestJSONErrorsNameLocalCause(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	t.Setenv("TERRASCALE_API_KEY", "")
	for _, tc := range []struct {
		args       []string
		code, want string
	}{
		{[]string{"--json", "whoami"}, "not_signed_in", "ts auth login"},
		{[]string{"--json", "-yes", "keys", "revoke", "01ARZ3NDEKTSV4RRFFQ69G5FAV"}, "not_signed_in", "ts auth login"},
		{[]string{"--json", "db", "delete", "app"}, "consent_required", "pass -yes"},
		{[]string{"--json", "keys", "revoke", "nope"}, "invalid_arguments", "canonical ULID"},
	} {
		var out bytes.Buffer
		err := runWithInput(t.Context(), tc.args, &out, forbiddenInput{}, io.Discard, false)
		var envelope struct{ Error commandError }
		if err == nil || json.Unmarshal(out.Bytes(), &envelope) != nil || envelope.Error.Code != tc.code || !strings.Contains(envelope.Error.Message, tc.want) {
			t.Errorf("run(%q) = %v, %q", tc.args, err, &out)
		}
	}
}

func TestPlacementWriteErrorNamesCommand(t *testing.T) {
	t.Parallel()
	err := placementError(&v1.APIError{Status: http.StatusConflict, Code: "placement_needs_two_regions", Message: "add replicaRegions"})
	var out bytes.Buffer
	if emitJSONError(&out, err) != nil || !strings.Contains(err.Error(), "ts db regions <db> <region>") || strings.Contains(err.Error(), "replicaRegions") ||
		!strings.Contains(out.String(), `"code":"placement_needs_two_regions"`) || !strings.Contains(out.String(), "ts db regions") {
		t.Fatalf("placement error = %v, %q", err, &out)
	}
	other := errors.New("other")
	if placementError(other) != other {
		t.Fatal("unrelated error changed")
	}
}

func TestBackupDestinationGetNotSet(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusNotFound)
		io.WriteString(w, `{"error":{"code":"not_found","message":"Not Found"}}`)
	})
	err := backupDestination(t.Context(), c, managementProjectID, "get", nil, nil, io.Discard)
	var out bytes.Buffer
	if err == nil || !strings.Contains(err.Error(), "no backup destination is set") || emitJSONError(&out, err) != nil ||
		!strings.Contains(out.String(), `"code":"not_found"`) || !strings.Contains(out.String(), "no backup destination is set") {
		t.Fatalf("destination get = %v, %q", err, &out)
	}
}

func TestChangesEmptyCursorStartsAtRetainedHistory(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Has("cursor") {
			t.Errorf("cursor sent: %q", r.URL.RawQuery)
		}
		w.WriteHeader(http.StatusGone)
		io.WriteString(w, `{"error":{"code":"cursor_expired","message":"expired"}}`)
	})
	if err := tailChangesFrom(t.Context(), c.Database(managementProjectID, "orders"), "empty", nil, io.Discard); !errors.Is(err, v1.ErrCursorExpired) {
		t.Fatal(err)
	}
}
