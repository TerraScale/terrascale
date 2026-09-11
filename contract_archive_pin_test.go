package sdk

import (
	"os"
	"strings"
	"testing"
)

func TestContractArchivePin(t *testing.T) {
	bytes, err := os.ReadFile("CONTRACT_ARCHIVE_SHA256")
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.TrimSpace(string(bytes)); got != "e7a8bcbe752d814006c52fd40df055d54ac9adb7e3c1e5050630951d8f3498af" {
		t.Fatalf("archive SHA-256 = %q", got)
	}
}
