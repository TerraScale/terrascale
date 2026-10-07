package core

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"regexp"
	"strings"

	"terrascale.tech/sdk/v1"
)

// terraBaseID matches the lowercase UUIDs TerraBase gives its resources.
// Dashboard records use prefixed IDs (db_…) instead.
var terraBaseID = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)

// SystemDatabase reports whether a database record mirrors a TerraBase
// database by its TerraBase ID: a database of a TerraScale service, which a
// platform admin's sign-in (or import-org) mirrored. Its contents live in
// TerraBase, never in the record.
func SystemDatabase(db Database) bool { return terraBaseID.MatchString(db.ID) }

// ErrSystemReadOnly refuses changes to a system database: TerraBase lets the
// dashboard read them with a ts-auth token, and their services own them.
var ErrSystemReadOnly = fmt.Errorf("%w: system databases are read-only in the dashboard", ErrForbidden)

type userTokenKey struct{}

// WithUserToken carries the request's ts-auth access token lookup. It runs
// only when a feature needs the token.
func WithUserToken(ctx context.Context, token func() (string, bool)) context.Context {
	return context.WithValue(ctx, userTokenKey{}, token)
}

// System returns the TerraBase Management API acting as the signed-in
// platform admin, with the admin's ts-auth access token, for reading the
// system databases. TerraBase checks that the admin belongs to their
// organization.
func (d Deps) System(ctx context.Context, p Principal) (*v1.ManagementUser, error) {
	if !p.PlatformAdmin {
		return nil, ErrForbidden
	}
	if d.Management == nil {
		return nil, fmt.Errorf("%w: TerraBase Management is not configured", ErrDataPlane)
	}
	token, _ := ctx.Value(userTokenKey{}).(func() (string, bool))
	if token == nil {
		// MCP and API callers present a dashboard access token, which
		// TerraBase does not accept.
		return nil, fmt.Errorf("%w: system data is shown on the dashboard's pages only", ErrDataPlane)
	}
	access, ok := token()
	if !ok {
		return nil, fmt.Errorf("%w: sign in again to read system data", ErrDataPlane)
	}
	return d.Management.WithToken(access), nil
}

// SystemCollections lists a system database of the principal's
// organization with each collection's live document count and bytes.
func (d Deps) SystemCollections(ctx context.Context, p Principal, db Database) ([]v1.CollectionStats, error) {
	management, err := d.System(ctx, p)
	if err != nil {
		return nil, err
	}
	stats, err := management.Collections(ctx, p.OrganizationID, db.ProjectID, db.ID)
	if err != nil {
		return nil, ProviderError{Action: "system collection list", Err: err}
	}
	return stats, nil
}

// SystemUnavailable is what a page or tool says when TerraBase did not
// report a system database. Provider details, such as TerraBase's internal
// URLs, go to the log only.
func SystemUnavailable(err error) string {
	switch {
	case errors.Is(err, ErrForbidden):
		return "System data is for platform admins."
	case errors.Is(err, ErrDataPlane):
		return strings.TrimPrefix(err.Error(), ErrDataPlane.Error()+": ")
	}
	slog.Warn("read system database", "error", err)
	return "TerraBase did not answer in time. Reload the page to try again."
}

// secretFields name document members whose values are credentials or
// derive from one. The match is a substring of the lowercased name without
// _ and -, and errs toward masking: password and token hashes, refresh
// tokens, client secrets, API, access, signing and private keys, sealed
// keys, credentials, cookies, nonces, OTP and TOTP seeds, JWTs and token
// payloads.
var secretFields = []string{
	"password", "secret", "hash", "token", "refresh", "payload", "otp", "seed", "private", "referenceid",
	"ciphertext", "key", "credential", "cookie", "nonce", "jwt", "salt", "signature",
}

// fieldName normalizes a member name for secretFields.
var fieldName = strings.NewReplacer("_", "", "-", "")

// Masked is shown in place of a secret value.
const Masked = "••••••"

// MaskSecrets replaces, at any depth, the value of every member whose name
// names a secret, so system documents never show credentials.
func MaskSecrets(value any) any {
	switch v := value.(type) {
	case map[string]any:
		for key, member := range v {
			if member != nil && SecretField(key) {
				v[key] = Masked
			} else {
				v[key] = MaskSecrets(member)
			}
		}
	case []any:
		for i, item := range v {
			v[i] = MaskSecrets(item)
		}
	}
	return value
}

// SecretField reports whether a document member's name names a secret.
func SecretField(name string) bool {
	return containsAny(strings.ToLower(fieldName.Replace(name)), secretFields)
}

func containsAny(s string, parts []string) bool {
	for _, part := range parts {
		if strings.Contains(s, part) {
			return true
		}
	}
	return false
}
