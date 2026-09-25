package apikeys

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	json "encoding/json/v2"
	"net/http"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

// CredentialIssuer is the unpublished live producer key boundary. The current
// application does not inject one because the producer has no pinned public
// issuance contract; live requests fail explicitly until that exists.
type CredentialIssuer interface {
	Create(context.Context, core.Principal, sdk.CreateProjectAPIKeyRequest) (*sdk.ProjectAPIKeySecret, error)
	Rotate(context.Context, core.Principal, string) (*sdk.ProjectAPIKeySecret, error)
	Revoke(context.Context, core.Principal, string) (*sdk.ProjectAPIKeyRevocation, error)
}

func Mount(mux *http.ServeMux, deps core.Deps) { MountWithIssuer(mux, deps, nil) }

func MountWithIssuer(mux *http.ServeMux, deps core.Deps, issuer CredentialIssuer) {
	mountPages(mux, deps)
	root := "/api/v1/projects/{projectID}/auth/api-keys"
	mux.HandleFunc("GET "+root, func(w http.ResponseWriter, r *http.Request) {
		_, project, account, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		keys := make([]sdk.ProjectAPIKey, 0, len(project.APIKeys))
		for _, key := range project.APIKeys {
			if key.Kind == "m2m" {
				continue
			}
			keys = append(keys, publicKey(key, account.User.Email))
		}
		slices.SortFunc(keys, func(a, b sdk.ProjectAPIKey) int { return strings.Compare(a.Name, b.Name) })
		core.WriteJSON(w, http.StatusOK, sdk.ProjectAPIKeyPage{APIKeys: keys})
	})
	mux.HandleFunc("POST "+root, func(w http.ResponseWriter, r *http.Request) {
		p, project, account, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 32<<10)
		var input sdk.CreateProjectAPIKeyRequest
		if err := json.UnmarshalRead(r.Body, &input); err != nil {
			http.Error(w, "invalid JSON request", http.StatusBadRequest)
			return
		}
		if err := validateInput(project, input); err != nil {
			writeError(w, err)
			return
		}
		if !deps.Demo {
			if issuer == nil {
				http.Error(w, "live credential issuance is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
				return
			}
			issued, err := issuer.Create(r.Context(), p, input)
			if err != nil {
				http.Error(w, "credential issuance failed", http.StatusBadGateway)
				return
			}
			core.WriteJSON(w, http.StatusCreated, issued)
			return
		}
		key, secret, err := createDemoKey(deps, p, input)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusCreated, sdk.ProjectAPIKeySecret{APIKey: publicKey(key, account.User.Email), Secret: secret})
	})
	mux.HandleFunc("POST "+root+"/{keyID}/rotate", func(w http.ResponseWriter, r *http.Request) {
		p, project, account, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		keyID := r.PathValue("keyID")
		if key, ok := project.APIKeys[keyID]; !ok || key.Kind == "m2m" {
			writeError(w, core.ErrNotFound)
			return
		}
		if !deps.Demo {
			if issuer == nil {
				http.Error(w, "live credential rotation is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
				return
			}
			issued, err := issuer.Rotate(r.Context(), p, keyID)
			if err != nil {
				http.Error(w, "credential rotation failed", http.StatusBadGateway)
				return
			}
			core.WriteJSON(w, http.StatusOK, issued)
			return
		}
		key, secret, err := rotateDemoKey(deps, p, keyID)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, sdk.ProjectAPIKeySecret{APIKey: publicKey(key, account.User.Email), Secret: secret})
	})
	mux.HandleFunc("DELETE "+root+"/{keyID}", func(w http.ResponseWriter, r *http.Request) {
		p, project, _, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		keyID := r.PathValue("keyID")
		if key, ok := project.APIKeys[keyID]; !ok || key.Kind == "m2m" {
			writeError(w, core.ErrNotFound)
			return
		}
		if !deps.Demo {
			if issuer == nil {
				http.Error(w, "live credential revocation is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
				return
			}
			result, err := issuer.Revoke(r.Context(), p, keyID)
			if err != nil {
				http.Error(w, "credential revocation failed", http.StatusBadGateway)
				return
			}
			core.WriteJSON(w, http.StatusOK, result)
			return
		}
		err = revokeDemoKey(deps, p, keyID)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, sdk.ProjectAPIKeyRevocation{KeyID: keyID, Revoked: true})
	})
}

func createDemoKey(deps core.Deps, p core.Principal, input sdk.CreateProjectAPIKeyRequest) (core.APIKey, string, error) {
	return createDemoCredential(deps, p, input, "api-key")
}

func createDemoCredential(deps core.Deps, p core.Principal, input sdk.CreateProjectAPIKeyRequest, kind string) (core.APIKey, string, error) {
	prefix := "tsk_demo_"
	if kind == "m2m" {
		prefix = "tsm_demo_"
	}
	secret := prefix + strings.TrimPrefix(core.NewID(""), "_")
	key := core.APIKey{ID: core.NewID("key"), Kind: kind, ProjectID: p.ProjectID, DatabaseID: input.DatabaseID, Name: strings.TrimSpace(input.Name), Prefix: secret[:min(len(secret), 16)], LastFour: secret[len(secret)-4:], SecretHash: hash(secret), Scopes: slices.Clone(input.Scopes), CreatedAt: time.Now().UTC()}
	err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if err := validateInput(project, input); err != nil {
			return err
		}
		if project.APIKeys == nil {
			project.APIKeys = make(map[string]core.APIKey)
		}
		project.APIKeys[key.ID] = key
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
	return key, secret, err
}

func rotateDemoKey(deps core.Deps, p core.Principal, keyID string) (core.APIKey, string, error) {
	secret := "tsk_demo_" + strings.TrimPrefix(core.NewID(""), "_")
	var key core.APIKey
	err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		key, ok := project.APIKeys[keyID]
		if !ok || !key.RevokedAt.IsZero() {
			return core.ErrNotFound
		}
		if key.Kind == "m2m" {
			secret = "tsm_demo_" + strings.TrimPrefix(core.NewID(""), "_")
		}
		key.SecretHash = hash(secret)
		key.Prefix = secret[:min(len(secret), 16)]
		key.LastFour = secret[len(secret)-4:]
		project.APIKeys[keyID] = key
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
	return key, secret, err
}

func revokeDemoKey(deps core.Deps, p core.Principal, keyID string) error {
	return deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		key, ok := project.APIKeys[keyID]
		if !ok {
			return core.ErrNotFound
		}
		key.RevokedAt = time.Now().UTC()
		project.APIKeys[keyID] = key
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

func selected(deps core.Deps, r *http.Request) (core.Principal, core.Project, core.Account, error) {
	p, ok := deps.Sessions.BearerPrincipal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.Account{}, core.ErrUnauthorized
	}
	if r.PathValue("projectID") != p.ProjectID {
		return core.Principal{}, core.Project{}, core.Account{}, core.ErrNotFound
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, core.Account{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, project, a, err
}

func validateInput(project core.Project, input sdk.CreateProjectAPIKeyRequest) error {
	if strings.TrimSpace(input.Name) == "" || len(input.Name) > 128 || len(input.Scopes) == 0 || len(input.Scopes) > 16 {
		return core.ErrInvalid
	}
	for _, scope := range input.Scopes {
		if strings.TrimSpace(scope) == "" || len(scope) > 128 {
			return core.ErrInvalid
		}
	}
	if input.DatabaseID != "" {
		if db, ok := project.Databases[input.DatabaseID]; !ok || db.ProjectID != project.ID {
			return core.ErrNotFound
		}
	}
	return nil
}

func hash(secret string) string {
	digest := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(digest[:])
}

func publicKey(key core.APIKey, createdBy string) sdk.ProjectAPIKey {
	return sdk.ProjectAPIKey{ID: key.ID, ProjectID: key.ProjectID, Name: key.Name, Prefix: key.Prefix, LastFour: key.LastFour, Scopes: slices.Clone(key.Scopes), DatabaseID: key.DatabaseID, CreatedBy: createdBy, CreatedAt: key.CreatedAt.Format(time.RFC3339), ExpiresAt: formatTime(key.ExpiresAt), RevokedAt: formatTime(key.RevokedAt), Active: key.RevokedAt.IsZero() && (key.ExpiresAt.IsZero() || time.Now().Before(key.ExpiresAt))}
}

func formatTime(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.Format(time.RFC3339)
}

func writeError(w http.ResponseWriter, err error) {
	if !core.WriteAccessError(w, err) {
		http.Error(w, "invalid input", http.StatusBadRequest)
	}
}
