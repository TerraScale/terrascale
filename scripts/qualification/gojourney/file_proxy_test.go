package main

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestFileProxyMatchesPinnedSDK(t *testing.T) {
	mod, err := os.ReadFile("go.mod")
	if err != nil {
		t.Fatal(err)
	}
	const prefix = "require terrascale.tech/sdk "
	var version string
	for _, line := range strings.Split(string(mod), "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), prefix) {
			version = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(line), prefix))
			break
		}
	}
	if version == "" {
		t.Fatal("go.mod has no pinned SDK version")
	}
	zipPath := filepath.Join("file-proxy", "terrascale.tech", "sdk", "@v", version+".zip")
	zipBytes, err := os.ReadFile(zipPath)
	if err != nil {
		t.Fatalf("read pinned SDK ZIP: %v", err)
	}
	wantBytes, err := os.ReadFile(filepath.Join("file-proxy", version+".zip.sha256"))
	if err != nil {
		t.Fatalf("read pinned SDK ZIP digest: %v", err)
	}
	want := strings.Fields(string(wantBytes))
	if len(want) != 2 || len(want[0]) != sha256.Size*2 {
		t.Fatalf("invalid ZIP digest record %q", string(wantBytes))
	}
	gotSum := sha256.Sum256(zipBytes)
	got := hex.EncodeToString(gotSum[:])
	if got != want[0] {
		t.Fatalf("SDK ZIP SHA-256 = %s, want %s (%s)", got, want[0], fmt.Sprint(want[1]))
	}
}
