package auth

import (
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"strconv"
	"strings"
)

const (
	passwordHashVersion = "pbkdf2-sha256"
	passwordIterations  = 600_000
	passwordKeyLength   = 32
	passwordSaltLength  = 16
)

func HashPassword(password string) (string, error) {
	salt := make([]byte, passwordSaltLength)
	rand.Read(salt)
	key, err := pbkdf2.Key(sha256.New, password, salt, passwordIterations, passwordKeyLength)
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
	actual, err := pbkdf2.Key(sha256.New, password, salt, iterations, len(expected))
	if err != nil {
		return false
	}
	return subtle.ConstantTimeCompare(actual, expected) == 1
}
