package apikeys

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"maps"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/v1"
)

const (
	kindAPIKey = "api-key"
	kindM2M    = "m2m"
)

var errLiveCredentials = errors.New("TerraBase data keys cannot be rotated, and machine clients are not issued yet")

// terraBaseScopes maps a key's scopes onto TerraBase's data key scopes;
// the dashboard's database:* name the same access.
// TODO(TS-589, TS-599): ts-auth API keys replace TerraBase data keys.
var terraBaseScopes = map[string]string{
	"database:read": "documents:read", "database:write": "documents:write",
	"documents:read": "documents:read", "documents:write": "documents:write",
}

// issuerError is a live credential request the producer rejected or failed.
type issuerError struct {
	action string
	err    error
}

func (e issuerError) Error() string { return e.action + " failed: " + e.err.Error() }
func (e issuerError) Unwrap() error { return e.err }

// credential is a project API key or M2M client without its secret.
type credential struct {
	ProjectAPIKey
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
// handlers and MCP tools both call it. Outside the demo, API keys are
// TerraBase data keys of the project's TerraBase project, managed as the
// signed-in user.
type Service struct {
	deps core.Deps
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
	keys, createdBy := slices.Collect(maps.Values(project.APIKeys)), a.User.Email
	if !s.deps.LocalResources() {
		// TerraBase does not say who created a key.
		if keys, err = s.terraBaseKeys(ctx, project); err != nil {
			return nil, err
		}
		createdBy = ""
	}
	search := strings.ToLower(strings.TrimSpace(filter.Search))
	list := make([]credential, 0, len(keys))
	for _, key := range keys {
		item := credential{ProjectAPIKey: publicKey(key, createdBy), Kind: key.Kind}
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
func (s Service) Create(ctx context.Context, p core.Principal, kind string, input CreateProjectAPIKeyRequest) (ProjectAPIKeySecret, error) {
	a, project, err := s.selected(ctx, p)
	if err != nil {
		return ProjectAPIKeySecret{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return ProjectAPIKeySecret{}, err
	}
	if err := validateInput(project, input); err != nil {
		return ProjectAPIKeySecret{}, err
	}
	if !s.deps.LocalResources() {
		if kind == kindM2M {
			return ProjectAPIKeySecret{}, errLiveCredentials
		}
		return s.issue(ctx, p, project, input)
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
		return ProjectAPIKeySecret{}, err
	}
	return ProjectAPIKeySecret{APIKey: publicKey(key, a.User.Email), Secret: secret}, nil
}

// Rotate replaces a live credential's secret and returns the new one. A
// non-empty kind restricts which credential kind may be rotated.
func (s Service) Rotate(ctx context.Context, p core.Principal, keyID, kind string) (ProjectAPIKeySecret, error) {
	if !s.deps.LocalResources() {
		return ProjectAPIKeySecret{}, errLiveCredentials
	}
	a, _, err := s.existing(ctx, p, keyID, kind)
	if err != nil {
		return ProjectAPIKeySecret{}, err
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
		return ProjectAPIKeySecret{}, err
	}
	return ProjectAPIKeySecret{APIKey: publicKey(key, a.User.Email), Secret: secret}, nil
}

// Revoke permanently disables a credential. A non-empty kind restricts which
// credential kind may be revoked.
func (s Service) Revoke(ctx context.Context, p core.Principal, keyID, kind string) (ProjectAPIKeyRevocation, error) {
	if !s.deps.LocalResources() {
		return s.revoke(ctx, p, keyID, kind)
	}
	if _, _, err := s.existing(ctx, p, keyID, kind); err != nil {
		return ProjectAPIKeyRevocation{}, err
	}
	err := s.update(ctx, p, func(project *core.Project) error {
		key, ok := project.APIKeys[keyID]
		if !ok {
			return core.ErrNotFound
		}
		key.RevokedAt = time.Now().UTC()
		project.APIKeys[keyID] = key
		return nil
	})
	if err != nil {
		return ProjectAPIKeyRevocation{}, err
	}
	return ProjectAPIKeyRevocation{KeyID: keyID, Revoked: true}, nil
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

func validateInput(project core.Project, input CreateProjectAPIKeyRequest) error {
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

func publicKey(key core.APIKey, createdBy string) ProjectAPIKey {
	return ProjectAPIKey{
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

// terraBaseKeys lists the data keys of the project's TerraBase project in
// every region it is registered in, without the dashboard's own.
func (s Service) terraBaseKeys(ctx context.Context, project core.Project) ([]core.APIKey, error) {
	regions, err := s.deps.DataKeyRegions(ctx, project)
	if err != nil {
		return nil, issuerError{action: "credential list", err: err}
	}
	databases := make(map[string]string, len(project.Databases))
	for _, db := range project.Databases {
		databases[db.TerraBaseID] = db.ID
	}
	org, proj := project.TerraBaseIDs(project.OrganizationID)
	var keys []core.APIKey
	for _, region := range regions {
		m, err := s.deps.Manage(ctx, region)
		if err != nil {
			return nil, issuerError{action: "credential list", err: err}
		}
		for opts := (v1.PageOptions{Limit: 100}); ; {
			page, err := m.ProjectAPIKeys(ctx, org, proj, opts)
			if err != nil {
				return nil, issuerError{action: "credential list", err: err}
			}
			for _, key := range page.Items {
				if key.ID == project.DataKeys[region.ID].ID {
					continue
				}
				keys = append(keys, core.APIKey{
					ID: key.ID, Kind: kindAPIKey, ProjectID: project.ID, DatabaseID: databases[key.DatabaseID], Name: key.Name,
					Prefix: "tb_", LastFour: key.LastFour, Scopes: key.Scopes, CreatedAt: key.CreatedAt,
				})
			}
			if opts.After = page.Next; opts.After == "" {
				break
			}
		}
	}
	return keys, nil
}

// issue mints a TerraBase data key for the project in its region, or for one
// of its databases in that database's home region.
func (s Service) issue(ctx context.Context, p core.Principal, project core.Project, input CreateProjectAPIKeyRequest) (ProjectAPIKeySecret, error) {
	request := v1.ProjectAPIKeyRequest{Name: strings.TrimSpace(input.Name)}
	for _, scope := range input.Scopes {
		mapped, ok := terraBaseScopes[strings.TrimSpace(scope)]
		if !ok {
			return ProjectAPIKeySecret{}, core.Invalid("TerraBase keys take the scopes database:read and database:write")
		}
		if !slices.Contains(request.Scopes, mapped) {
			request.Scopes = append(request.Scopes, mapped)
		}
	}
	region, err := s.deps.Region(ctx, project.Region)
	if input.DatabaseID != "" {
		db := project.Databases[input.DatabaseID]
		if db, err = s.deps.Ensure(ctx, p, project, db); err != nil {
			return ProjectAPIKeySecret{}, err
		}
		request.DatabaseID = db.TerraBaseID
		region, err = s.deps.Home(ctx, project, db)
	}
	if err != nil {
		return ProjectAPIKeySecret{}, err
	}
	// The project's keys are listed in the regions the dashboard holds its
	// own data key in.
	m, org, proj, err := s.deps.ManageProject(ctx, p, project, region)
	if err != nil {
		return ProjectAPIKeySecret{}, err
	}
	created, err := m.CreateProjectAPIKey(ctx, org, proj, request)
	if err != nil {
		return ProjectAPIKeySecret{}, issuerError{action: "credential issuance", err: err}
	}
	key := core.APIKey{
		ID: created.Key.ID, Kind: kindAPIKey, ProjectID: project.ID, DatabaseID: input.DatabaseID, Name: created.Key.Name,
		Prefix: "tb_", LastFour: created.Key.LastFour, Scopes: created.Key.Scopes, CreatedAt: created.Key.CreatedAt,
	}
	return ProjectAPIKeySecret{APIKey: publicKey(key, ""), Secret: created.Secret}, nil
}

// revoke revokes a TerraBase data key of the project in the region holding
// it. The dashboard's own keys are not the project's to revoke.
func (s Service) revoke(ctx context.Context, p core.Principal, keyID, kind string) (ProjectAPIKeyRevocation, error) {
	_, project, err := s.selected(ctx, p)
	if err != nil {
		return ProjectAPIKeyRevocation{}, err
	}
	if err := core.Can(ctx, s.deps.Store, p, core.Organization.CanManage); err != nil {
		return ProjectAPIKeyRevocation{}, err
	}
	missing := fmt.Errorf("%w: credential %q", core.ErrNotFound, keyID)
	if kind == kindM2M || slices.ContainsFunc(slices.Collect(maps.Values(project.DataKeys)), func(key core.DataKey) bool { return key.ID == keyID }) {
		return ProjectAPIKeyRevocation{}, missing
	}
	regions, err := s.deps.DataKeyRegions(ctx, project)
	if err != nil {
		return ProjectAPIKeyRevocation{}, err
	}
	org, proj := project.TerraBaseIDs(p.OrganizationID)
	for _, region := range regions {
		m, err := s.deps.Manage(ctx, region)
		if err != nil {
			return ProjectAPIKeyRevocation{}, err
		}
		switch err := m.RevokeProjectAPIKey(ctx, org, proj, keyID); {
		case err == nil:
			return ProjectAPIKeyRevocation{KeyID: keyID, Revoked: true}, nil
		case !errors.Is(err, v1.ErrNotFound) && !errors.Is(err, v1.ErrUnknownRoute):
			return ProjectAPIKeyRevocation{}, issuerError{action: "credential revocation", err: err}
		}
	}
	return ProjectAPIKeyRevocation{}, missing
}
