package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

func TestCLIContextPersistenceAndOverrides(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "terrascale", "context.json")
	c := cliContext{"01M5N9000000000000000000AA", "01M5N9000000000000000000AB", "01M5N9000000000000000000AC", "orders"}
	if err := saveCLIContext(path, c); err != nil {
		t.Fatal(err)
	}
	got, err := readCLIContext(path)
	if err != nil || got != c {
		t.Fatalf("context = %+v, %v", got, err)
	}
	if runtime.GOOS != "windows" {
		for _, entry := range []struct {
			path string
			mode os.FileMode
		}{{path, 0o600}, {filepath.Dir(path), 0o700}} {
			if info, err := os.Stat(entry.path); err != nil || info.Mode().Perm() != entry.mode {
				t.Fatalf("context permissions: %v, %v", info, err)
			}
		}
	}
	org, project, db, err := contextDefaults(c, c.AccountID, "", "")
	if err != nil || org != c.OrganizationID || project != c.ProjectID || db != "orders" {
		t.Fatalf("defaults = %q %q %q, %v", org, project, db, err)
	}
	org, project, db, err = contextDefaults(c, "other-account", "", "explicit-project")
	if err != nil || org != "" || project != "explicit-project" || db != "" {
		t.Fatalf("explicit overrides = %q %q %q, %v", org, project, db, err)
	}
	if _, _, _, err := contextDefaults(c, "other-account", "", ""); err == nil {
		t.Fatal("another account used saved context")
	}
	if !usesDatabaseOperand("functions invoke") || usesDatabaseOperand("db create") {
		t.Fatal("saved database must apply to existing database operations")
	}
	args, err := contextDatabase([]string{"@", "people"}, c.Database)
	if err != nil || strings.Join(args, " ") != "orders people" {
		t.Fatalf("database operand = %q, %v", args, err)
	}
	if _, err := contextDatabase([]string{"@"}, ""); err == nil {
		t.Fatal("missing database accepted")
	}
	var out bytes.Buffer
	if err := clearCLIContext(path, &out); err != nil {
		t.Fatal(err)
	}
	if c, err := readCLIContext(path); err != nil || c != (cliContext{}) {
		t.Fatalf("cleared context = %+v, %v", c, err)
	}
}

func TestCLIContextCorruptionDoesNotReflectInput(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "context.json")
	if err := os.WriteFile(path, []byte(`{"accountId":"sensitive-invalid-value"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := readCLIContext(path); err == nil || strings.Contains(err.Error(), "sensitive-invalid-value") {
		t.Fatalf("corrupt context diagnostic = %v", err)
	}
}

func TestCLIContextResolvesCanonicalIDsAndRejectsAmbiguity(t *testing.T) {
	t.Parallel()
	org := "01M5N9000000000000000000AA"
	project := "01M5N9000000000000000000AB"
	u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/v1/terrascale/organizations":
			json.NewEncoder(w).Encode(tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{
				{ID: org, Name: "duplicate", CreatedBy: org}, {ID: project, Name: "duplicate", CreatedBy: org},
			}})
		case "/v1/terrascale/organizations/" + org + "/projects":
			json.NewEncoder(w).Encode(tsauth.Page[tsauth.Project]{Items: []tsauth.Project{{ID: project, OrganizationID: org, CreatedBy: org, Name: "app"}}})
		default:
			t.Errorf("unexpected request %s", r.URL)
			w.WriteHeader(http.StatusNotFound)
		}
	})
	c, err := resolveCLIContext(t.Context(), u, org, org, "app")
	if err != nil || c.OrganizationID != org || c.ProjectID != project || c.AccountID != org {
		t.Fatalf("resolved context = %+v, %v", c, err)
	}
	if _, err := resolveCLIContext(t.Context(), u, org, "duplicate", "app"); err == nil {
		t.Fatal("ambiguous organization name accepted")
	}
	if _, err := resolveCLIContext(t.Context(), u, org, org, "missing"); err == nil {
		t.Fatal("unreachable project accepted")
	}
}

func TestCLIContextShowPropagatesWriterFailure(t *testing.T) {
	t.Parallel()
	if err := showCLIContext(filepath.Join(t.TempDir(), "missing.json"), managementBrokenWriter{}); err != io.ErrClosedPipe {
		t.Fatalf("context output error = %v", err)
	}
}
