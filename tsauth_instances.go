package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
)

// Operator instance bootstrap (ts-auth POST /v1/instances). The Client's
// AccessToken is the deployment's operator bearer key, not an instance sk_
// key. ts-auth has no instance get, list, suspend, delete or key rotation
// routes yet, so only creation is exposed.

// TSAuthInstance is a ts-auth instance as returned by CreateInstance.
type TSAuthInstance struct {
	ID   string `json:"id"`
	Slug string `json:"slug"`
	Name string `json:"name"`
}

// TSAuthCreatedInstance is the CreateInstance answer. APIKey is the
// instance's initial sk_ management key; a replay with the same
// Idempotency-Key returns the same key, so store it before acknowledging.
type TSAuthCreatedInstance struct {
	Instance TSAuthInstance `json:"instance"`
	APIKey   string         `json:"api_key"`
}

func (c TSAuthCreatedInstance) String() string {
	return fmt.Sprintf("TSAuthCreatedInstance{Instance:%+v APIKey:[REDACTED]}", c.Instance)
}

func (c TSAuthCreatedInstance) GoString() string { return c.String() }

// CreateInstance error codes. The returned error wraps both the matching
// sentinel (errors.Is) and the *APIError (errors.As).
var (
	// ErrTSAuthInstanceExists: another instance already owns the slug (409
	// instance_exists).
	ErrTSAuthInstanceExists = errors.New("sdk: ts-auth instance slug already exists")
	// ErrTSAuthIdempotencyKeyReused: the Idempotency-Key was already used
	// with a different slug or name (409 idempotency_key_reused).
	ErrTSAuthIdempotencyKeyReused = errors.New("sdk: ts-auth idempotency key reused with a different request")
	// ErrTSAuthBootstrapKeyConflict: the initial API key of the original
	// request no longer matches, for example because it was revoked (409
	// bootstrap_key_conflict).
	ErrTSAuthBootstrapKeyConflict = errors.New("sdk: ts-auth bootstrap API key conflict")
	// ErrTSAuthUnauthorized: the operator bearer key was rejected (401
	// unauthorized).
	ErrTSAuthUnauthorized = errors.New("sdk: ts-auth operator credentials rejected")
	// ErrTSAuthUnavailable: the outcome is still not visible after the
	// retries (503 temporarily_unavailable). Retry with the same key.
	ErrTSAuthUnavailable = errors.New("sdk: ts-auth temporarily unavailable")
)

var tsAuthInstanceErrors = map[string]error{
	"instance_exists":         ErrTSAuthInstanceExists,
	"idempotency_key_reused":  ErrTSAuthIdempotencyKeyReused,
	"bootstrap_key_conflict":  ErrTSAuthBootstrapKeyConflict,
	"unauthorized":            ErrTSAuthUnauthorized,
	"temporarily_unavailable": ErrTSAuthUnavailable,
}

// CreateInstance creates a ts-auth instance and its initial API key with the
// operator bearer key (Config.AccessToken). idempotencyKey must be 16-128
// visible ASCII characters and stable across retries of the same request:
// a replay returns the same instance and API key. 503 and other transient
// failures are retried with the same key (at least three times, or
// Config.MaxRetryAttempts when larger). The slug is lower-cased by the server
// and must be 3-48 characters of [a-z0-9-].
func (t *TSAuthClient) CreateInstance(ctx context.Context, slug, name, idempotencyKey string) (*TSAuthCreatedInstance, error) {
	if ctx == nil {
		return nil, &ValidationError{Field: "context", Message: "cannot be nil"}
	}
	if len(idempotencyKey) < 16 || len(idempotencyKey) > 128 || strings.ContainsFunc(idempotencyKey, func(r rune) bool { return r < 0x21 || r > 0x7e }) {
		return nil, &ValidationError{Field: "idempotency_key", Message: "must be 16-128 visible ASCII characters"}
	}
	normalized := strings.ToLower(strings.TrimSpace(slug))
	if len(normalized) < 3 || len(normalized) > 48 || strings.ContainsFunc(normalized, func(r rune) bool { return !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9' || r == '-') }) {
		return nil, &ValidationError{Field: "slug", Message: "must be 3-48 characters of [a-z0-9-]"}
	}
	if strings.TrimSpace(name) == "" {
		return nil, &ValidationError{Field: "name", Message: "cannot be empty"}
	}
	base, err := tsAuthResolveBase(t.c.cfg)
	if err != nil {
		return nil, err
	}
	payload, err := json.Marshal(map[string]string{"slug": slug, "name": name})
	if err != nil {
		return nil, err
	}
	bearer := tsAuthBearer(t.c.cfg)
	if t.c.cfg.TokenProvider != nil {
		bearer = t.c.cfg.TokenProvider()
	}
	const route = "tsauth.instances.create"
	var out TSAuthCreatedInstance
	err = t.c.tsAuthSendAuthorized(ctx, base, http.MethodPost, "/v1/instances", nil, payload, "application/json",
		tsAuthAuthorization{bearer: bearer, idempotencyKey: idempotencyKey}, &out, route)
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) {
			if sentinel, ok := tsAuthInstanceErrors[apiErr.Code]; ok {
				return nil, fmt.Errorf("%w: %w", sentinel, err)
			}
		}
		return nil, err
	}
	if out.Instance.ID == "" || out.Instance.Slug == "" || out.APIKey == "" {
		return nil, fmt.Errorf("sdk: %s: incomplete response", route)
	}
	return &out, nil
}
