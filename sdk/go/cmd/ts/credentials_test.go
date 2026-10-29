package main

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

func TestSaveCredentials(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"replace", "symlink", "write", "short", "serialize", "create"} {
		t.Run(kind, func(t *testing.T) {
			t.Parallel()
			parent := t.TempDir()
			dir := filepath.Join(parent, "terrascale")
			path := filepath.Join(dir, "cli.json")
			old := []byte(`{"accessToken":"old"}`)
			if err := os.Mkdir(dir, 0o755); err != nil {
				t.Fatal(err)
			}
			victim := filepath.Join(parent, "untouched.json")
			if err := os.WriteFile(victim, old, 0o644); err != nil {
				t.Fatal(err)
			}
			if kind == "symlink" {
				if err := os.Symlink(victim, path); err != nil {
					t.Fatal(err)
				}
			} else if kind != "create" {
				if err := os.WriteFile(path, old, 0o666); err != nil {
					t.Fatal(err)
				}
				if err := os.Chmod(path, 0o666); err != nil {
					t.Fatal(err)
				}
			}
			if kind == "create" {
				if err := os.Remove(dir); err != nil {
					t.Fatal(err)
				}
			}
			parentInfo, _ := os.Stat(parent)
			victimInfo, _ := os.Stat(victim)
			tokens := &tsauth.Tokens{AccessToken: "new", RefreshToken: "refresh"}
			write := (*os.File).Write
			if kind == "write" || kind == "short" {
				write = func(f *os.File, data []byte) (int, error) {
					n, err := f.Write(data[:1])
					if kind == "write" {
						err = errors.New("injected write failure")
					}
					return n, err
				}
			}
			if kind == "serialize" {
				tokens.Expiry = time.Date(10000, 1, 1, 0, 0, 0, 0, time.UTC)
			}
			err := saveCredentials(path, tokens, write)
			failed := kind == "write" || kind == "short" || kind == "serialize"
			if (err != nil) != failed {
				t.Fatalf("save error = %v", err)
			}
			data, err := os.ReadFile(path)
			if err != nil {
				t.Fatal(err)
			}
			if failed {
				if string(data) != string(old) {
					t.Fatalf("old credentials changed: %s", data)
				}
			} else {
				var got credentials
				if err := json.Unmarshal(data, &got); err != nil || got.AccessToken != "new" {
					t.Fatalf("credentials = %s, %v", data, err)
				}
				for name, mode := range map[string]os.FileMode{path: 0o600, dir: 0o700} {
					info, err := os.Lstat(name)
					if err != nil || info.Mode().Perm() != mode || info.Mode()&os.ModeSymlink != 0 {
						t.Fatalf("permissions for %s: %v, %v", name, info, err)
					}
				}
			}
			for name, before := range map[string]os.FileInfo{parent: parentInfo, victim: victimInfo} {
				after, err := os.Stat(name)
				if err != nil || before.Mode() != after.Mode() {
					t.Fatalf("unrelated permissions changed: %s", name)
				}
			}
			if data, err := os.ReadFile(victim); err != nil || string(data) != string(old) {
				t.Fatal("symlink target changed", err)
			}
			entries, err := os.ReadDir(dir)
			if err != nil || len(entries) != 1 || entries[0].Name() != "cli.json" {
				t.Fatalf("temporary file leaked: %v, %v", entries, err)
			}
		})
	}
}
