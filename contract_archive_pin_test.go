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
	if got := strings.TrimSpace(string(bytes)); got != "b0b0297f3facf5c0ed9cc17b86a6cec477db4babad47ac9a62ce7a2e2134502b" {
		t.Fatalf("archive SHA-256 = %q", got)
	}
}
