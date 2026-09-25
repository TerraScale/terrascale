package auth

import (
	"strings"
	"testing"
)

func TestPasswordHashIsSaltedAndRejectsWrongPassword(t *testing.T) {
	t.Parallel()

	const password = "correct horse battery staple"
	first, err := HashPassword(password)
	if err != nil {
		t.Fatalf("hashPassword() error = %v", err)
	}
	second, err := HashPassword(password)
	if err != nil {
		t.Fatalf("hashPassword() second error = %v", err)
	}
	if first == second {
		t.Fatal("hashPassword() reused the same salt")
	}
	if strings.Contains(first, password) {
		t.Fatal("stored hash contains the plaintext password")
	}
	if !VerifyPassword(first, password) {
		t.Fatal("verifyPassword() rejected the correct password")
	}
	if VerifyPassword(first, "incorrect horse battery staple") {
		t.Fatal("verifyPassword() accepted an incorrect password")
	}
}

func TestPasswordVerifierRejectsMalformedOrExpensiveHash(t *testing.T) {
	t.Parallel()

	for _, encoded := range []string{
		"",
		"unknown$600000$00000000000000000000000000000000$0000000000000000000000000000000000000000000000000000000000000000",
		"pbkdf2-sha256$1200001$00000000000000000000000000000000$0000000000000000000000000000000000000000000000000000000000000000",
		"pbkdf2-sha256$600000$short$not-hex",
	} {
		if VerifyPassword(encoded, "password") {
			t.Errorf("verifyPassword(%q) = true, want false", encoded)
		}
	}
}
