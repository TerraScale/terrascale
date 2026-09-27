package databases

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

var (
	errInvalidName  = core.Invalid("database names use 1-63 lowercase letters, digits, and inner hyphens")
	errNotReady     = fmt.Errorf("%w: only a ready database can be branched", core.ErrConflict)
	errProvisioning = fmt.Errorf("%w: database provisioning is in progress", core.ErrConflict)
	errConfirmName  = core.Invalid("type the database name to confirm deletion")
)

// Service owns the database lifecycle in a principal's selected project.
// HTTP handlers and MCP tools both call it.
type Service struct {
	deps core.Deps
}

func (s Service) selected(p core.Principal) (core.Account, core.Project, error) {
	if p.ProjectID == "" {
		return core.Account{}, core.Project{}, core.ErrNotFound
	}
	a, err := s.deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return a, project, err
}

// List returns the project's databases by name.
func (s Service) List(p core.Principal) ([]databaseView, error) {
	_, project, err := s.selected(p)
	if err != nil {
		return nil, err
	}
	list := make([]databaseView, 0, len(project.Databases))
	for _, db := range project.Databases {
		list = append(list, view(db))
	}
	slices.SortFunc(list, func(a, b databaseView) int { return strings.Compare(a.Name, b.Name) })
	return list, nil
}

// Detail returns one database with its storage, region, and backup summary.
func (s Service) Detail(p core.Principal, databaseID string) (databaseDetail, error) {
	a, project, err := s.selected(p)
	if err != nil {
		return databaseDetail{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		return databaseDetail{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	org := a.Organizations[p.OrganizationID]
	canManage := org.CanManage(p.UserID)
	info := makeDetailInfo(s.deps.Demo, p, a, project, db, canManage, canManage && org.CanProvisionResources(p.UserID))
	detail := databaseDetail{
		databaseView: view(db), Endpoint: db.Endpoint, Collections: info.Collections, Documents: info.Documents,
		BackupsEnabled: info.BackupsEnabled, BackupRetentionDays: db.BackupRetentionDays, CanManage: canManage, CanBranch: info.CanBranch,
	}
	for _, region := range info.Regions {
		detail.Regions = append(detail.Regions, region.Name)
	}
	return detail, nil
}

// Create provisions a database in the selected project. An empty region
// uses the project's primary region.
func (s Service) Create(ctx context.Context, p core.Principal, name, region string) (databaseView, error) {
	_, project, err := s.selected(p)
	if err != nil {
		return databaseView{}, err
	}
	name = strings.TrimSpace(name)
	if !core.ValidDatabaseName(name) {
		return databaseView{}, errInvalidName
	}
	if region == "" {
		region = project.Region
	}
	code, err := sdk.ParseRegionCode(region)
	if err != nil {
		return databaseView{}, core.Invalid(fmt.Sprintf("unknown region %q", region))
	}
	db := core.Database{
		ID: core.NewID("db"), ProjectID: p.ProjectID, Name: name, Region: string(code), Status: "provisioning",
		CreatedAt: time.Now().UTC(), Documents: make(map[string]map[string][]byte),
	}
	if s.deps.Demo {
		db.Status = "ready"
	}
	// The store refuses the commit with core.ErrDatabaseLimit when the plan's
	// database allowance is used up.
	err = s.deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanProvisionResources(p.UserID) {
			return core.ErrForbidden
		}
		for _, existing := range project.Databases {
			if existing.Name == name {
				return fmt.Errorf("%w: database %q already exists", core.ErrConflict, name)
			}
		}
		if project.Databases == nil {
			project.Databases = make(map[string]core.Database)
		}
		project.Databases[db.ID] = db
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
	if err != nil {
		return databaseView{}, err
	}
	if s.deps.Demo {
		return view(db), nil
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		_ = s.setStatus(p, db.ID, "failed")
		return databaseView{}, err
	}
	result, err := client.Databases.CreateDatabase(ctx, sdk.CreateDatabaseRequest{Name: name, Placement: []sdk.RegionCode{code}, OperationID: db.ID}, sdk.WithIdempotencyKey(db.ID))
	switch {
	case err != nil:
		_ = s.setStatus(p, db.ID, "failed")
		return databaseView{}, core.ProviderError{Action: "database provisioning", Err: err}
	case result.Operation != nil:
		db.Status = result.Operation.State
	case result.Database != nil:
		db.Status = "ready"
	default:
		_ = s.setStatus(p, db.ID, "failed")
		return databaseView{}, core.ProviderError{Action: "database provisioning", Err: errors.New("no identity returned")}
	}
	return view(db), s.setStatus(p, db.ID, db.Status)
}

// Branch copies a ready database into a new database in the same project.
// An empty name derives a preview branch name from the source.
func (s Service) Branch(ctx context.Context, p core.Principal, sourceID, name string) (databaseView, error) {
	_, project, err := s.selected(p)
	if err != nil {
		return databaseView{}, err
	}
	if !s.deps.Store.Can(p, core.Organization.CanManage) {
		return databaseView{}, core.ErrForbidden
	}
	source, ok := project.Databases[sourceID]
	if !ok {
		return databaseView{}, fmt.Errorf("%w: database %q", core.ErrNotFound, sourceID)
	}
	name = strings.TrimSpace(name)
	if name == "" {
		name = previewBranchName(source.Name)
	}
	if !core.ValidDatabaseName(name) {
		return databaseView{}, errInvalidName
	}
	if source.Status != "ready" {
		return databaseView{}, errNotReady
	}
	branch := core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: source.Region, Status: "provisioning", CreatedAt: time.Now().UTC()}
	if s.deps.Demo {
		branch.Status = "ready"
	}
	err = s.deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		org := a.Organizations[p.OrganizationID]
		if !org.CanProvisionResources(p.UserID) {
			return core.ErrForbidden
		}
		current, ok := project.Databases[source.ID]
		if !ok {
			return fmt.Errorf("%w: database %q", core.ErrNotFound, source.ID)
		}
		if current.Status != "ready" {
			return errNotReady
		}
		for _, db := range project.Databases {
			if db.Name == name {
				return fmt.Errorf("%w: database %q already exists", core.ErrConflict, name)
			}
		}
		if s.deps.Demo {
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
		return databaseView{}, err
	}
	if s.deps.Demo {
		return view(branch), nil
	}
	client, err := s.deps.DataPlane(p)
	if err != nil {
		_ = s.setStatus(p, branch.ID, "failed")
		return databaseView{}, err
	}
	result, err := client.DatabaseBranches.CreateBranch(ctx, source.Name, sdk.CreateBranchRequest{Name: name, OperationID: branch.ID}, sdk.WithIdempotencyKey(branch.ID))
	if err != nil {
		_ = s.setStatus(p, branch.ID, "failed")
		return databaseView{}, core.ProviderError{Action: "branch request", Err: err}
	}
	branch.Status = result.State
	return view(branch), s.setStatus(p, branch.ID, branch.Status)
}

// Delete removes a database once confirmName repeats its name.
func (s Service) Delete(ctx context.Context, p core.Principal, databaseID, confirmName string) error {
	_, project, err := s.selected(p)
	if err != nil {
		return err
	}
	if !s.deps.Store.Can(p, core.Organization.CanManage) {
		return core.ErrForbidden
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		return fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	if confirmName != db.Name {
		return errConfirmName
	}
	if provisioning(db.Status) {
		return errProvisioning
	}
	if !s.deps.Demo {
		client, err := s.deps.DataPlane(p)
		if err != nil {
			return err
		}
		if _, err := client.Databases.DeleteDatabase(ctx, db.Name, sdk.WithIdempotencyKey(core.NewID("del")), sdk.WithDeleteConfirmation(db.Name)); err != nil {
			return core.ProviderError{Action: "database deletion", Err: err}
		}
	}
	return s.deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if _, ok := project.Databases[db.ID]; !ok {
			return core.ErrNotFound
		}
		project.RemoveDatabase(db.ID, time.Now().UTC())
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

func (s Service) setStatus(p core.Principal, databaseID, status string) error {
	return s.deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		db, ok := project.Databases[databaseID]
		if !ok {
			return core.ErrNotFound
		}
		db.Status = status
		project.Databases[databaseID] = db
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
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
