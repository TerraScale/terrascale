package auth

import (
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"runtime"
	"strconv"
	"strings"
)

const (
	passwordHashVersion = "pbkdf2-sha256"
	passwordIterations  = 600_000
	passwordKeyLength   = 32
	passwordSaltLength  = 16
)

// derivations bounds concurrent key derivations to half the CPUs. Each one
// takes a CPU for a noticeable time, so a burst of password checks waits
// here instead of starving every other request.
var derivations = make(chan struct{}, max(1, runtime.GOMAXPROCS(0)/2))

func deriveKey(password string, salt []byte, iterations, length int) ([]byte, error) {
	derivations <- struct{}{}
	defer func() { <-derivations }()
	return pbkdf2.Key(sha256.New, password, salt, iterations, length)
}

func HashPassword(password string) (string, error) {
	salt := make([]byte, passwordSaltLength)
	rand.Read(salt)
	key, err := deriveKey(password, salt, passwordIterations, passwordKeyLength)
	if err != nil {
		return "", err
	}
	return strings.Join([]string{
		passwordHashVersion,
		strconv.Itoa(passwordIterations),
		hex.EncodeToString(salt),
		hex.EncodeToString(key),
	}, "$"), nil
}

func VerifyPassword(encoded, password string) bool {
	version, remainder, ok := strings.Cut(encoded, "$")
	if !ok || version != passwordHashVersion {
		return false
	}
	iterationText, remainder, ok := strings.Cut(remainder, "$")
	if !ok {
		return false
	}
	iterations, err := strconv.Atoi(iterationText)
	if err != nil || iterations < passwordIterations || iterations > 1_200_000 {
		return false
	}
	saltText, keyText, ok := strings.Cut(remainder, "$")
	if !ok || strings.Contains(keyText, "$") {
		return false
	}
	salt, err := hex.DecodeString(saltText)
	if err != nil || len(salt) != passwordSaltLength {
		return false
	}
	expected, err := hex.DecodeString(keyText)
	if err != nil || len(expected) != passwordKeyLength {
		return false
	}
	actual, err := deriveKey(password, salt, iterations, len(expected))
	if err != nil {
		return false
	}
	return subtle.ConstantTimeCompare(actual, expected) == 1
}
