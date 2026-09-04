package terrabasego

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// The stub pins nothing yet: it only proves the contract bundle the Go gate
// depends on exists and names its version. Real pinning (generated stubs
// matching the manifest's contract version) arrives with the full E-1
// contract publish.
func TestContractBundleExists(t *testing.T) {
	t.Parallel()

	root := filepath.Join("..", "contracts")
	for _, artifact := range []string{
		"openapi.json",
		"manifest.json",
		filepath.Join("proto", "terrabase", "v1", "engine.proto"),
		filepath.Join("proto", "terrabase", "v1", "family.proto"),
	} {
		info, err := os.Stat(filepath.Join(root, artifact))
		if err != nil {
			t.Fatalf("stat %s: %v", artifact, err)
		}
		if info.Size() == 0 {
			t.Fatalf("%s is empty", artifact)
		}
	}

	raw, err := os.ReadFile(filepath.Join(root, "manifest.json"))
	if err != nil {
		t.Fatalf("read manifest.json: %v", err)
	}
	var manifest struct {
		ContractVersion string `json:"contractVersion"`
	}
	if err := json.Unmarshal(raw, &manifest); err != nil {
		t.Fatalf("parse manifest.json: %v", err)
	}
	if manifest.ContractVersion == "" {
		t.Fatal("manifest.json names no contract version")
	}
}
