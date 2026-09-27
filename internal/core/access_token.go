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

// CreateAccessToken issues a personal access token for the principal's user
// and returns it with its one-time secret.
func (s *Store) CreateAccessToken(p Principal, name string, lifetime time.Duration) (AccessToken, string, error) {
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
	now := time.Now().UTC()
	token := AccessToken{
		ID: NewID("pat"), Name: name, LastFour: secret[len(secret)-4:],
		SecretHash: hashAccessToken(secret), CreatedAt: now, ExpiresAt: now.Add(lifetime),
	}
	err := s.UpdateAs(p, func(a *Account) error {
		a.AccessTokens = slices.DeleteFunc(a.AccessTokens, func(t AccessToken) bool { return t.Expired(now) })
		if len(a.AccessTokens) >= MaxAccessTokens {
			return ErrConflict
		}
		a.AccessTokens = append(a.AccessTokens, token)
		return nil
	})
	if err != nil {
		return AccessToken{}, "", err
	}
	return token, secret, nil
}

// RevokeAccessToken deletes one of the principal's personal access tokens.
func (s *Store) RevokeAccessToken(p Principal, id string) error {
	return s.UpdateAs(p, func(a *Account) error {
		before := len(a.AccessTokens)
		a.AccessTokens = slices.DeleteFunc(a.AccessTokens, func(t AccessToken) bool { return t.ID == id })
		if len(a.AccessTokens) == before {
			return ErrNotFound
		}
		return nil
	})
}

// AccessTokenPrincipal resolves a personal access token secret to its
// account's principal for the account's current selection.
func (s *Store) AccessTokenPrincipal(secret string) (Principal, error) {
	if !strings.HasPrefix(secret, AccessTokenPrefix) {
		return Principal{}, ErrUnauthorized
	}
	hash := hashAccessToken(secret)
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.accounts[s.byAccessToken[hash]]
	if !ok {
		return Principal{}, ErrUnauthorized
	}
	index := slices.IndexFunc(a.AccessTokens, func(t AccessToken) bool { return t.SecretHash == hash })
	if index < 0 || a.AccessTokens[index].Expired(time.Now()) {
		return Principal{}, ErrUnauthorized
	}
	view := s.viewLocked(a)
	p := view.Principal()
	if err := authorize(view, p); err != nil {
		return Principal{}, ErrUnauthorized
	}
	return p, nil
}

func hashAccessToken(secret string) string {
	digest := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(digest[:])
}
