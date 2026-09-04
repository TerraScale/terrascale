package terrabasego

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The stub pins nothing yet: it only proves the contract the Go gate
// depends on exists. Real pinning (generated stubs matching
// CONTRACT_VERSION) arrives with the full E-1 contract publish.
func TestContractVersionFileExists(t *testing.T) {
	t.Parallel()

	path := filepath.Join("..", "contracts", "CONTRACT_VERSION")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	if strings.TrimSpace(string(raw)) == "" {
		t.Fatalf("%s is empty", path)
	}
}
