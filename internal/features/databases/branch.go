package databases

import (
	"errors"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func mountBranch(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("POST /api/v1/dashboard/databases/branch", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, project, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		source, ok := project.Databases[r.FormValue("databaseId")]
		if !ok {
			writeError(w, core.ErrNotFound)
			return
		}
		failed := func(status int, code string) {
			FormFailed(w, r, status, TabPath(source.ID, "branches", nil), code, nil)
		}
		name := strings.TrimSpace(r.FormValue("name"))
		if _, hasName := r.Form["name"]; !hasName {
			name = previewBranchName(source.Name)
		}
		if !core.ValidDatabaseName(name) {
			failed(http.StatusBadRequest, ErrInvalidName)
			return
		}
		if source.Status != "ready" {
			failed(http.StatusConflict, ErrNotReady)
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
				branch.Documents = core.CloneDocuments(current.Documents)
				branch.Schemas = core.CloneSchemas(current.Schemas)
				branch.Indexes = core.CloneIndexes(current.Indexes)
				branch.DocumentHistory = core.CloneHistory(current.DocumentHistory)
			}
			project.Databases[branch.ID] = branch
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			status, code := failure(err)
			if errors.Is(err, core.ErrNotFound) {
				status, code = http.StatusConflict, ErrNotReady
			}
			failed(status, code)
			return
		}
		if !deps.Demo {
			if deps.SDK == nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err != nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				failed(http.StatusServiceUnavailable, ErrUnavailable)
				return
			}
			result, err := client.DatabaseBranches.CreateBranch(r.Context(), source.Name, sdk.CreateBranchRequest{Name: name, OperationID: branch.ID}, sdk.WithIdempotencyKey(branch.ID))
			if err != nil {
				_ = setStatus(deps, p, branch.ID, "failed")
				failed(http.StatusBadGateway, ErrProvider)
				return
			}
			branch.Status = result.State
			if err := setStatus(deps, p, branch.ID, branch.Status); err != nil {
				failed(failure(err))
				return
			}
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(branch.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, view(branch))
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
