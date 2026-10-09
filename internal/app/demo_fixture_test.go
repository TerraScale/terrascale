package app

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestDemoAPIKeysUseCanonicalIDs(t *testing.T) {
	m2m := 0
	for key, credential := range marioAPIKeys(core.NewID(), core.NewID()) {
		if key != credential.ID || !core.ValidID(key) {
			t.Fatalf("demo %s credential has noncanonical identity %q / %q", credential.Kind, key, credential.ID)
		}
		if credential.Kind == "m2m" {
			m2m++
		}
	}
	if m2m != 2 {
		t.Fatalf("demo has %d M2M clients, want 2", m2m)
	}
}
