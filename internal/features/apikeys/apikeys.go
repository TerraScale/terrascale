package apikeys

import (
	json "encoding/json/v2"
	"errors"
	"net/http"

	"terrascale.tech/dashboard/internal/core"
)

// Mount owns the project credential routes and tools. The
// /api/v1/projects routes are the SDK's project API key contract, so they
// only address API keys, never M2M clients.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mountPages(mux, deps, service)
	root := "/api/v1/projects/{projectID}/auth/api-keys"
	mux.HandleFunc("GET "+root, func(w http.ResponseWriter, r *http.Request) {
		p, err := bearerPrincipal(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		list, err := service.List(r.Context(), p, credentialFilter{Kind: kindAPIKey})
		if err != nil {
			writeError(w, err)
			return
		}
		keys := make([]ProjectAPIKey, 0, len(list))
		for _, key := range list {
			keys = append(keys, key.ProjectAPIKey)
		}
		core.WriteJSON(w, http.StatusOK, ProjectAPIKeyPage{APIKeys: keys})
	})
	mux.HandleFunc("POST "+root, func(w http.ResponseWriter, r *http.Request) {
		p, err := bearerPrincipal(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 32<<10)
		var input CreateProjectAPIKeyRequest
		if err := json.UnmarshalRead(r.Body, &input); err != nil {
			http.Error(w, "invalid JSON request", http.StatusBadRequest)
			return
		}
		created, err := service.Create(r.Context(), p, kindAPIKey, input)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusCreated, created)
	})
	mux.HandleFunc("POST "+root+"/{keyID}/rotate", func(w http.ResponseWriter, r *http.Request) {
		p, err := bearerPrincipal(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		rotated, err := service.Rotate(r.Context(), p, r.PathValue("keyID"), kindAPIKey)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, rotated)
	})
	mux.HandleFunc("DELETE "+root+"/{keyID}", func(w http.ResponseWriter, r *http.Request) {
		p, err := bearerPrincipal(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		revoked, err := service.Revoke(r.Context(), p, r.PathValue("keyID"), kindAPIKey)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, revoked)
	})
}

// bearerPrincipal authenticates an SDK request whose path must name the
// session's selected project.
func bearerPrincipal(deps core.Deps, r *http.Request) (core.Principal, error) {
	p, ok := deps.Sessions.BearerPrincipal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.ErrUnauthorized
	}
	if r.PathValue("projectID") != p.ProjectID {
		return core.Principal{}, core.ErrNotFound
	}
	return p, nil
}

func writeError(w http.ResponseWriter, err error) {
	var issuer issuerError
	switch {
	case errors.Is(err, errLiveCredentials):
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
	case errors.As(err, &issuer):
		http.Error(w, issuer.action+" failed", http.StatusBadGateway)
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, err.Error(), http.StatusBadRequest)
	default:
		http.Error(w, "invalid input", http.StatusBadRequest)
	}
}
