package databases

import (
	"net/http"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func mountBranch(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("POST /api/v1/dashboard/databases/branch", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		source, ok := project.Databases[r.FormValue("databaseId")]
		if !ok {
			writeError(w, core.ErrNotFound)
			return
		}
		name := strings.TrimSpace(r.FormValue("name"))
		if _, hasName := r.Form["name"]; !hasName {
			name = previewBranchName(source.Name)
		}
		if !namePattern.MatchString(name) || source.Status != "ready" {
			http.Error(w, "valid branch name and ready source database are required", http.StatusBadRequest)
			return
		}
		branch := core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: source.Region, Status: "provisioning", CreatedAt: time.Now().UTC()}
		if deps.Demo {
			branch.Status = "ready"
		}
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := a.Organizations[p.OrganizationID]
			if !org.CanProvisionResources(p.UserID) {
				return core.ErrForbidden
			}
			current, ok := project.Databases[source.ID]
			if !ok || current.Status != "ready" {
				return core.ErrNotFound
			}
			for _, db := range project.Databases {
				if db.Name == name {
					return core.ErrConflict
				}
			}
			if deps.Demo {
				branch.Documents = cloneDocuments(current.Documents)
				branch.Schemas = cloneSchemas(current.Schemas)
				branch.Indexes = cloneIndexes(current.Indexes)
				branch.DocumentHistory = cloneHistory(current.DocumentHistory)
			}
			project.Databases[branch.ID] = branch
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				http.Error(w, "live data plane unavailable", http.StatusServiceUnavailable)
				return
			}
			result, err := client.DatabaseBranches.CreateBranch(r.Context(), source.Name, sdk.CreateBranchRequest{Name: name, OperationID: branch.ID}, sdk.WithIdempotencyKey(branch.ID))
			if err != nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				http.Error(w, "branch request failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			branch.Status = result.State
			if err := setStatus(deps, p, branch.ID, branch.Status); err != nil {
				writeError(w, err)
				return
			}
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(branch.ID), http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusCreated, view(branch))
	})
}

func previewBranchName(sourceName string) string {
	const suffix = "-preview-branch"
	const maxNameLength = 63
	if len(sourceName) > maxNameLength-len(suffix) {
		sourceName = strings.TrimRight(sourceName[:maxNameLength-len(suffix)], "-")
	}
	return sourceName + suffix
}

func cloneDocuments(input map[string]map[string][]byte) map[string]map[string][]byte {
	out := make(map[string]map[string][]byte, len(input))
	for collection, docs := range input {
		out[collection] = make(map[string][]byte, len(docs))
		for id, body := range docs {
			out[collection][id] = slices.Clone(body)
		}
	}
	return out
}

func cloneSchemas(input map[string][]byte) map[string][]byte {
	out := make(map[string][]byte, len(input))
	for collection, body := range input {
		out[collection] = slices.Clone(body)
	}
	return out
}

func cloneIndexes(input map[string][]string) map[string][]string {
	out := make(map[string][]string, len(input))
	for collection, fields := range input {
		out[collection] = slices.Clone(fields)
	}
	return out
}

func cloneHistory(input map[string]map[string][][]byte) map[string]map[string][][]byte {
	out := make(map[string]map[string][][]byte, len(input))
	for collection, docs := range input {
		out[collection] = make(map[string][][]byte, len(docs))
		for id, versions := range docs {
			out[collection][id] = make([][]byte, len(versions))
			for index, body := range versions {
				out[collection][id][index] = slices.Clone(body)
			}
		}
	}
	return out
}
