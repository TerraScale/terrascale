package apikeys

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

const (
	kindAPIKey = "api-key"
	kindM2M    = "m2m"
)

var errLiveCredentials = errors.New("live credentials are unavailable until a published producer contract exists")

// issuerError is a live credential request the producer rejected or failed.
type issuerError struct {
	action string
	err    error
}

func (e issuerError) Error() string { return e.action + " failed: " + e.err.Error() }
func (e issuerError) Unwrap() error { return e.err }

// credential is a project API key or M2M client without its secret.
type credential struct {
	sdk.ProjectAPIKey
	Kind string `json:"kind" jsonschema:"api-key for a project API key or m2m for a machine-to-machine client."`
}

// credentialFilter narrows List. Empty fields match every credential.
type credentialFilter struct {
	Kind       string
	Status     string
	DatabaseID string
	Search     string
}

// Service owns project credentials in a principal's selected project. HTTP
// handlers and MCP tools both call it.
type Service struct {
	deps   core.Deps
	issuer CredentialIssuer
}

func (s Service) selected(ctx context.Context, p core.Principal) (core.Account, core.Project, error) {
	if p.ProjectID == "" {
		return core.Account{}, core.Project{}, core.ErrNotFound
	}
	a, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return a, project, err
}

// List returns the project's credentials matching filter by name.
func (s Service) List(ctx context.Context, p core.Principal, filter credentialFilter) ([]credential, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return nil, err
	}
	search := strings.ToLower(strings.TrimSpace(filter.Search))
	list := make([]credential, 0, len(project.APIKeys))
	for _, key := range project.APIKeys {
		item := credential{ProjectAPIKey: publicKey(key, a.User.Email), Kind: key.Kind}
		if filter.Kind != "" && key.Kind != filter.Kind ||
			filter.Status == "active" && !item.Active || filter.Status == "inactive" && item.Active ||
			filter.DatabaseID != "" && key.DatabaseID != filter.DatabaseID ||
			search != "" && !strings.Contains(strings.ToLower(item.Name+" "+item.Prefix), search) {
			continue
		}
		list = append(list, item)
	}
	slices.SortFunc(list, func(a, b credential) int { return strings.Compare(a.Name, b.Name) })
	return list, nil
}

// Create issues a credential of kind and returns its one-time secret.
func (s Service) Create(ctx context.Context, p core.Principal, kind string, input sdk.CreateProjectAPIKeyRequest) (sdk.ProjectAPIKeySecret, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	if err := validateInput(project, input); err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	if !s.deps.LocalResources() {
		if s.issuer == nil || kind == kindM2M {
			return sdk.ProjectAPIKeySecret{}, errLiveCredentials
		}
		issued, err := s.issuer.Create(ctx, p, input)
		if err != nil {
			return sdk.ProjectAPIKeySecret{}, issuerError{action: "credential issuance", err: err}
		}
		return *issued, nil
	}
	prefix := "tsk_demo_"
	if kind == kindM2M {
		prefix = "tsm_demo_"
	}
	secret := prefix + strings.TrimPrefix(core.NewID(""), "_")
	key := core.APIKey{
		ID: core.NewID("key"), Kind: kind, ProjectID: p.ProjectID, DatabaseID: input.DatabaseID, Name: strings.TrimSpace(input.Name),
		Prefix: secret[:min(len(secret), 16)], LastFour: secret[len(secret)-4:], SecretHash: hash(secret),
		Scopes: slices.Clone(input.Scopes), CreatedAt: time.Now().UTC(),
	}
	err = s.update(ctx, p, func(project *core.Project) error {
		if err := validateInput(*project, input); err != nil {
			return err
		}
		if project.APIKeys == nil {
			project.APIKeys = make(map[string]core.APIKey)
		}
		project.APIKeys[key.ID] = key
		return nil
	})
	if err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	return sdk.ProjectAPIKeySecret{APIKey: publicKey(key, a.User.Email), Secret: secret}, nil
}

// Rotate replaces a live credential's secret and returns the new one. A
// non-empty kind restricts which credential kind may be rotated.
func (s Service) Rotate(ctx context.Context, p core.Principal, keyID, kind string) (sdk.ProjectAPIKeySecret, error) {
	a, existing, err := s.existing(ctx, p, keyID, kind)
	if err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	if !s.deps.LocalResources() {
		// The producer issuer only manages project API keys.
		if s.issuer == nil || existing.Kind == kindM2M {
			return sdk.ProjectAPIKeySecret{}, errLiveCredentials
		}
		issued, err := s.issuer.Rotate(ctx, p, keyID)
		if err != nil {
			return sdk.ProjectAPIKeySecret{}, issuerError{action: "credential rotation", err: err}
		}
		return *issued, nil
	}
	var key core.APIKey
	var secret string
	err = s.update(ctx, p, func(project *core.Project) error {
		current, ok := project.APIKeys[keyID]
		if !ok || !current.RevokedAt.IsZero() {
			return fmt.Errorf("%w: active credential %q", core.ErrNotFound, keyID)
		}
		prefix := "tsk_demo_"
		if current.Kind == kindM2M {
			prefix = "tsm_demo_"
		}
		secret = prefix + strings.TrimPrefix(core.NewID(""), "_")
		current.SecretHash = hash(secret)
		current.Prefix = secret[:min(len(secret), 16)]
		current.LastFour = secret[len(secret)-4:]
		project.APIKeys[keyID] = current
		key = current
		return nil
	})
	if err != nil {
		return sdk.ProjectAPIKeySecret{}, err
	}
	return sdk.ProjectAPIKeySecret{APIKey: publicKey(key, a.User.Email), Secret: secret}, nil
}

// Revoke permanently disables a credential. A non-empty kind restricts which
// credential kind may be revoked.
func (s Service) Revoke(ctx context.Context, p core.Principal, keyID, kind string) (sdk.ProjectAPIKeyRevocation, error) {
	_, existing, err := s.existing(ctx, p, keyID, kind)
	if err != nil {
		return sdk.ProjectAPIKeyRevocation{}, err
	}
	if !s.deps.LocalResources() {
		if s.issuer == nil || existing.Kind == kindM2M {
			return sdk.ProjectAPIKeyRevocation{}, errLiveCredentials
		}
		result, err := s.issuer.Revoke(ctx, p, keyID)
		if err != nil {
			return sdk.ProjectAPIKeyRevocation{}, issuerError{action: "credential revocation", err: err}
		}
		return *result, nil
	}
	err = s.update(ctx, p, func(project *core.Project) error {
		key, ok := project.APIKeys[keyID]
		if !ok {
			return core.ErrNotFound
		}
		key.RevokedAt = time.Now().UTC()
		project.APIKeys[keyID] = key
		return nil
	})
	if err != nil {
		return sdk.ProjectAPIKeyRevocation{}, err
	}
	return sdk.ProjectAPIKeyRevocation{KeyID: keyID, Revoked: true}, nil
}

// existing checks that the principal may manage credentials and returns the
// credential keyID names, which must be of kind when kind is not empty.
func (s Service) existing(ctx context.Context, p core.Principal, keyID, kind string) (core.Account, core.APIKey, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return core.Account{}, core.APIKey{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return core.Account{}, core.APIKey{}, err
	}
	key, ok := project.APIKeys[keyID]
	if !ok || kind != "" && key.Kind != kind {
		return core.Account{}, core.APIKey{}, fmt.Errorf("%w: credential %q", core.ErrNotFound, keyID)
	}
	return a, key, nil
}

// update applies mutate to the selected project after rechecking that the
// principal manages its organization.
func (s Service) update(ctx context.Context, p core.Principal, mutate func(*core.Project) error) error {
	return s.deps.Store.UpdateAs(ctx, p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if err := mutate(&project); err != nil {
			return err
		}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

// errUnknownDatabase rejects a credential limited to a database that is not in
// the project.
var errUnknownDatabase = fmt.Errorf("%w: database", core.ErrNotFound)

func validateInput(project core.Project, input sdk.CreateProjectAPIKeyRequest) error {
	if strings.TrimSpace(input.Name) == "" || len(input.Name) > 128 {
		return core.Invalid("credential names use 1-128 characters")
	}
	if len(input.Scopes) == 0 || len(input.Scopes) > 16 {
		return core.Invalid("credentials need 1-16 scopes")
	}
	for _, scope := range input.Scopes {
		if strings.TrimSpace(scope) == "" || len(scope) > 128 {
			return core.Invalid("scopes use 1-128 characters")
		}
	}
	if input.DatabaseID != "" {
		if db, ok := project.Databases[input.DatabaseID]; !ok || db.ProjectID != project.ID {
			return fmt.Errorf("%w %q", errUnknownDatabase, input.DatabaseID)
		}
	}
	return nil
}

func hash(secret string) string {
	digest := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(digest[:])
}

func publicKey(key core.APIKey, createdBy string) sdk.ProjectAPIKey {
	return sdk.ProjectAPIKey{
		ID: key.ID, ProjectID: key.ProjectID, Name: key.Name, Prefix: key.Prefix, LastFour: key.LastFour,
		Scopes: slices.Clone(key.Scopes), DatabaseID: key.DatabaseID, CreatedBy: createdBy,
		CreatedAt: key.CreatedAt.Format(time.RFC3339), ExpiresAt: formatTime(key.ExpiresAt), RevokedAt: formatTime(key.RevokedAt),
		Active: key.RevokedAt.IsZero() && (key.ExpiresAt.IsZero() || time.Now().Before(key.ExpiresAt)),
	}
}

func formatTime(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.Format(time.RFC3339)
}
