package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const fakeProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

var fakeAPIKey = "0" + strings.Repeat("0", 25) + "_" + strings.Repeat("a", 52)

// useKeyFile writes content to a 0600 file in a test dir and points TS_BENCH_KEY_FILE at it.
func useKeyFile(t *testing.T, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "bench-key.json")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestBenchCredentialsStagesKeyFile(t *testing.T) {
	t.Parallel()
	want := fmt.Sprintf(`{"api_key":"%s","project":"%s"}`, fakeAPIKey, fakeProject)
	src := useKeyFile(t, want)
	path, cleanup, err := benchCredentialsFrom(src)
	if err != nil {
		t.Fatal(err)
	}
	if path == src {
		t.Fatal("clients were handed the operator's file instead of a staged copy")
	}
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode().Perm() != 0o600 {
		t.Fatalf("credentials file mode %o, want 600", fi.Mode().Perm())
	}
	dir := filepath.Dir(path)
	di, err := os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if di.Mode().Perm() != 0o700 {
		t.Fatalf("credentials dir mode %o, want 700", di.Mode().Perm())
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != want {
		t.Fatalf("credentials file = %q, want %q", got, want)
	}
	cleanup()
	if _, err := os.Stat(dir); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("cleanup left %s: %v", dir, err)
	}
}

func TestBenchCredentialsRequiresPath(t *testing.T) {
	t.Parallel()
	if _, _, err := benchCredentialsFrom(""); err == nil || !strings.Contains(err.Error(), benchKeyEnv) {
		t.Fatalf("empty path: error = %v, want naming %s", err, benchKeyEnv)
	}
}

func TestBenchCredentialsRejectsUnreadableFile(t *testing.T) {
	t.Parallel()
	for name, path := range map[string]string{
		"missing":   filepath.Join(t.TempDir(), "absent.json"),
		"directory": t.TempDir(),
	} {
		t.Run(name, func(t *testing.T) {
			if _, _, err := benchCredentialsFrom(path); err == nil || !strings.Contains(err.Error(), benchKeyEnv) {
				t.Fatalf("error = %v, want naming %s", err, benchKeyEnv)
			}
		})
	}
}

func TestBenchCredentialsRejectsInvalidKey(t *testing.T) {
	t.Parallel()
	cases := map[string]string{
		"not json":    `not json SECRETVALUE`,
		"bad api_key": `{"api_key":"bad_SECRETVALUE","project":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}`,
		"lowercase project": fmt.Sprintf(`{"api_key":"%s","project":"%s"}`,
			fakeAPIKey, strings.ToLower(fakeProject)),
		"missing project": fmt.Sprintf(`{"api_key":"%s"}`, fakeAPIKey),
	}
	for name, content := range cases {
		t.Run(name, func(t *testing.T) {
			path := useKeyFile(t, content)
			_, _, err := benchCredentialsFrom(path)
			if !errors.Is(err, errBenchKey) {
				t.Fatalf("error = %v, want errBenchKey", err)
			}
			for _, secret := range []string{"SECRETVALUE", fakeAPIKey} {
				if strings.Contains(err.Error(), secret) {
					t.Fatalf("error leaks secret content: %v", err)
				}
			}
		})
	}
}
