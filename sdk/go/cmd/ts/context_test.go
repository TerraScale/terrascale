package main

import (
	"bytes"

	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
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
