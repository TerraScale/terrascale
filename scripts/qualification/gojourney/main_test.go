package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestVerifyCurrentSourceInputs(t *testing.T) {
	if err := verifySourceInputs("."); err != nil {
		t.Fatalf("verifySourceInputs() = %v", err)
	}
}

func TestVerifySourceInputs(t *testing.T) {
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "input.txt"), []byte("original"), 0o600); err != nil {
		t.Fatal(err)
	}
	digest := "0684d71c7f5d6b0f6f3fbe1f7f53f7c0f4c4f17d2f7c8f89ccf0b7f9f7c9e6c2"
	if err := os.WriteFile(filepath.Join(root, "source-input-sha256.txt"), []byte(digest+"  input.txt\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := verifySourceInputs(root); err == nil || !strings.Contains(err.Error(), "hash mismatch") {
		t.Fatalf("verifySourceInputs() error = %v, want hash mismatch", err)
	}
}

func TestVerifySourceInputsRejectsMissingInput(t *testing.T) {
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "source-input-sha256.txt"), []byte(strings.Repeat("0", 64)+"  missing.txt\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := verifySourceInputs(root); err == nil || !strings.Contains(err.Error(), "read source input") {
		t.Fatalf("verifySourceInputs() error = %v, want missing input error", err)
	}
}
