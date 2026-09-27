package auth

import "testing"

func TestTestPasswordCostStillVerifiesHashes(t *testing.T) {
	full := passwordIterations
	t.Cleanup(func() { passwordIterations = full })

	strong, err := HashPassword("correct-horse-battery")
	if err != nil {
		t.Fatal(err)
	}
	UseTestPasswordCost()
	if passwordIterations >= full {
		t.Fatalf("test binaries hash with %d iterations, want fewer than %d", passwordIterations, full)
	}
	cheap, err := HashPassword("correct-horse-battery")
	if err != nil {
		t.Fatal(err)
	}
	for name, hash := range map[string]string{"full-cost hash": strong, "test-cost hash": cheap} {
		if !VerifyPassword(hash, "correct-horse-battery") || VerifyPassword(hash, "wrong-horse-battery") {
			t.Errorf("%s does not verify the right password only", name)
		}
	}
}
