package core

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"slices"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

// AccessTokenPrefix marks personal access tokens so secret scanners and
// humans can recognize them.
const AccessTokenPrefix = "tspat_"

// MaxAccessTokens bounds how many live personal access tokens one account
// may hold.
const MaxAccessTokens = 25

// AccessToken is a personal access token that authenticates its account's
// user on the MCP endpoint. Only the SHA-256 digest of the secret is stored.
type AccessToken struct {
	ID         string
	Name       string
	LastFour   string
	SecretHash string
	CreatedAt  time.Time
	ExpiresAt  time.Time
}

// Expired reports whether the token no longer authenticates at now.
func (t AccessToken) Expired(now time.Time) bool {
	return !now.Before(t.ExpiresAt)
}

// NewAccessToken validates a token name and lifetime and returns a new token
// with its one-time secret, for Store.CreateAccessToken to add to an
// account.
func NewAccessToken(name string, lifetime time.Duration, now time.Time) (AccessToken, string, error) {
	name = strings.TrimSpace(name)
	if name == "" || utf8.RuneCountInString(name) > 64 || strings.ContainsFunc(name, unicode.IsControl) {
		return AccessToken{}, "", Invalid("access token names use 1-64 printable characters")
	}
	if lifetime <= 0 {
		return AccessToken{}, "", Invalid("access tokens need a positive lifetime")
	}
	var random [32]byte
	rand.Read(random[:])
	secret := AccessTokenPrefix + base64.RawURLEncoding.EncodeToString(random[:])
	return AccessToken{
		ID: NewID("pat"), Name: name, LastFour: secret[len(secret)-4:],
		SecretHash: HashAccessToken(secret), CreatedAt: now, ExpiresAt: now.Add(lifetime),
	}, secret, nil
}

// AddAccessToken drops the account's expired tokens and adds token, unless
// the account already holds MaxAccessTokens live ones.
func AddAccessToken(a *Account, token AccessToken, now time.Time) error {
	a.AccessTokens = slices.DeleteFunc(a.AccessTokens, func(t AccessToken) bool { return t.Expired(now) })
	if len(a.AccessTokens) >= MaxAccessTokens {
		return ErrConflict
	}
	a.AccessTokens = append(a.AccessTokens, token)
	return nil
}

// RemoveAccessToken deletes the account's token id.
func RemoveAccessToken(a *Account, id string) error {
	before := len(a.AccessTokens)
	a.AccessTokens = slices.DeleteFunc(a.AccessTokens, func(t AccessToken) bool { return t.ID == id })
	if len(a.AccessTokens) == before {
		return ErrNotFound
	}
	return nil
}

// HashAccessToken returns the hex SHA-256 digest under which a token secret
// is stored and indexed.
func HashAccessToken(secret string) string {
	digest := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(digest[:])
}
